﻿using System;
using System.Globalization;
using CoreWCF;
using System.IO;
using System.Text;
using CoreWCF.Runtime;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace CoreWCF.Channels.Framing
{
    static class DecoderHelper
    {
        public static void ValidateSize(long size)
        {
            if (size <= 0)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ArgumentOutOfRangeException("size", size, SR.ValueMustBePositive));
            }
        }
    }

    struct IntDecoder
    {
        int value;
        short index;
        bool isValueDecoded;
        const int LastIndex = 4;

        public int Value
        {
            get
            {
                if (!isValueDecoded)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return value;
            }
        }

        public bool IsValueDecoded
        {
            get { return isValueDecoded; }
        }

        public void Reset()
        {
            index = 0;
            value = 0;
            isValueDecoded = false;
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            if (isValueDecoded)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
            }
            int bytesConsumed = 0;
            
            while (bytesConsumed < buffer.Length)
            {
                var data = buffer.First.Span;
                int next = data[0];
                value |= (next & 0x7F) << (index * 7);
                bytesConsumed++;
                if (index == LastIndex && (next & 0xF8) != 0)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataException(SR.FramingSizeTooLarge));
                }
                index++;
                if ((next & 0x80) == 0)
                {
                    isValueDecoded = true;
                    break;
                }
                buffer = buffer.Slice(buffer.GetPosition(1));
            }
            return bytesConsumed;
        }
    }

    abstract class StringDecoder
    {
        int encodedSize;
        byte[] encodedBytes;
        int bytesNeeded;
        string value;
        State currentState;
        IntDecoder sizeDecoder;
        int sizeQuota;
        int valueLengthInBytes;

        public StringDecoder(int sizeQuota)
        {
            this.sizeQuota = sizeQuota;
            sizeDecoder = new IntDecoder();
            Reset();
        }

        public bool IsValueDecoded
        {
            get { return currentState == State.Done; }
        }

        public string Value
        {
            get
            {
                if (currentState != State.Done)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return value;
            }
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);

            int bytesConsumed;
            switch (currentState)
            {
                case State.ReadingSize:
                    bytesConsumed = sizeDecoder.Decode(buffer);
                    if (sizeDecoder.IsValueDecoded)
                    {
                        encodedSize = sizeDecoder.Value;
                        if (encodedSize > sizeQuota)
                        {
                            Exception quotaExceeded = OnSizeQuotaExceeded(encodedSize);
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(quotaExceeded);
                        }
                        if (encodedBytes == null || encodedBytes.Length < encodedSize)
                        {
                            encodedBytes = Fx.AllocateByteArray(encodedSize);
                            value = null;
                        }
                        currentState = State.ReadingBytes;
                        bytesNeeded = encodedSize;
                    }
                    break;
                case State.ReadingBytes:
                    if (value != null && valueLengthInBytes == encodedSize && bytesNeeded == encodedSize &&
                        buffer.Length >= encodedSize && CompareBuffers(encodedBytes, buffer))
                    {
                        bytesConsumed = bytesNeeded;
                        OnComplete(value);
                    }
                    else
                    {
                        bytesConsumed = bytesNeeded;
                        if (buffer.Length < bytesNeeded)
                            bytesConsumed = (int)buffer.Length;
                        
                        Span<byte> span = encodedBytes;
                        Span<byte> slicedBytes = span.Slice(encodedSize - bytesNeeded, bytesConsumed);
                        var tempBuffer = buffer.Slice(0, bytesConsumed);
                        tempBuffer.CopyTo(slicedBytes);
                        bytesNeeded -= bytesConsumed;
                        if (bytesNeeded == 0)
                        {
                            value = Encoding.UTF8.GetString(encodedBytes, 0, encodedSize);
                            valueLengthInBytes = encodedSize;
                            OnComplete(value);
                        }
                    }
                    break;
                default:
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataException(SR.InvalidDecoderStateMachine));
            }

            return bytesConsumed;
        }

        protected virtual void OnComplete(string value)
        {
            currentState = State.Done;
        }

        static bool CompareBuffers(byte[] buffer1, ReadOnlySequence<byte> buffer2)
        {
            var buff = buffer2.ToArray();
            for (int i = 0; i < buffer1.Length; i++)
            {
                if (buffer1[i] != buff[i])
                {
                    return false;
                }
            }
            return true;
        }

        protected abstract Exception OnSizeQuotaExceeded(int size);

        public void Reset()
        {
            currentState = State.ReadingSize;
            sizeDecoder.Reset();
        }

        enum State
        {
            ReadingSize,
            ReadingBytes,
            Done,
        }
    }

    class ViaStringDecoder : StringDecoder
    {
        Uri via;

        public ViaStringDecoder(int sizeQuota)
            : base(sizeQuota)
        {
        }

        protected override Exception OnSizeQuotaExceeded(int size)
        {
            Exception result = new InvalidDataException(SR.Format(SR.FramingViaTooLong, size));
            FramingEncodingString.AddFaultString(result, FramingEncodingString.ViaTooLongFault);
            return result;
        }

        protected override void OnComplete(string value)
        {
            try
            {
                via = new Uri(value);
                base.OnComplete(value);
            }
            catch (UriFormatException exception)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidDataException(SR.Format(SR.FramingViaNotUri, value), exception));
            }
        }

        public Uri ValueAsUri
        {
            get
            {
                if (!IsValueDecoded)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return via;
            }
        }
    }

    class FaultStringDecoder : StringDecoder
    {
        internal const int FaultSizeQuota = 256;

        public FaultStringDecoder()
            : base(FaultSizeQuota)
        {
        }

        protected override Exception OnSizeQuotaExceeded(int size)
        {
            return new InvalidDataException(SR.Format(SR.FramingFaultTooLong, size));
        }

        public static Exception GetFaultException(string faultString, string via, string contentType)
        {
            if (faultString == FramingEncodingString.EndpointNotFoundFault)
            {
                return new EndpointNotFoundException(SR.Format(SR.EndpointNotFound, via));
            }
            else if (faultString == FramingEncodingString.ContentTypeInvalidFault)
            {
                return new ProtocolException(SR.Format(SR.FramingContentTypeMismatch, contentType, via));
            }
            else if (faultString == FramingEncodingString.ServiceActivationFailedFault)
            {
                return new ServiceActivationException(SR.Format(SR.Hosting_ServiceActivationFailed, via));
            }
            else if (faultString == FramingEncodingString.ConnectionDispatchFailedFault)
            {
                return new CommunicationException(SR.Format(SR.Sharing_ConnectionDispatchFailed, via));
            }
            else if (faultString == FramingEncodingString.EndpointUnavailableFault)
            {
                return new EndpointNotFoundException(SR.Format(SR.Sharing_EndpointUnavailable, via));
            }
            else if (faultString == FramingEncodingString.MaxMessageSizeExceededFault)
            {
                Exception inner = new QuotaExceededException(SR.FramingMaxMessageSizeExceeded);
                return new CommunicationException(inner.Message, inner);
            }
            else if (faultString == FramingEncodingString.UnsupportedModeFault)
            {
                return new ProtocolException(SR.Format(SR.FramingModeNotSupportedFault, via));
            }
            else if (faultString == FramingEncodingString.UnsupportedVersionFault)
            {
                return new ProtocolException(SR.Format(SR.FramingVersionNotSupportedFault, via));
            }
            else if (faultString == FramingEncodingString.ContentTypeTooLongFault)
            {
                Exception inner = new QuotaExceededException(SR.Format(SR.FramingContentTypeTooLongFault, contentType));
                return new CommunicationException(inner.Message, inner);
            }
            else if (faultString == FramingEncodingString.ViaTooLongFault)
            {
                Exception inner = new QuotaExceededException(SR.Format(SR.FramingViaTooLongFault, via));
                return new CommunicationException(inner.Message, inner);
            }
            else if (faultString == FramingEncodingString.ServerTooBusyFault)
            {
                return new ServerTooBusyException(SR.Format(SR.ServerTooBusy, via));
            }
            else if (faultString == FramingEncodingString.UpgradeInvalidFault)
            {
                return new ProtocolException(SR.Format(SR.FramingUpgradeInvalid, via));
            }
            else
            {
                return new ProtocolException(SR.Format(SR.FramingFaultUnrecognized, faultString));
            }
        }
    }

    class ContentTypeStringDecoder : StringDecoder
    {
        public ContentTypeStringDecoder(int sizeQuota)
            : base(sizeQuota)
        {
        }

        protected override Exception OnSizeQuotaExceeded(int size)
        {
            Exception result = new InvalidDataException(SR.Format(SR.FramingContentTypeTooLong, size));
            FramingEncodingString.AddFaultString(result, FramingEncodingString.ContentTypeTooLongFault);
            return result;
        }

        public static string GetString(FramingEncodingType type)
        {
            switch (type)
            {
                case FramingEncodingType.Soap11Utf8:
                    return FramingEncodingString.Soap11Utf8;
                case FramingEncodingType.Soap11Utf16:
                    return FramingEncodingString.Soap11Utf16;
                case FramingEncodingType.Soap11Utf16FFFE:
                    return FramingEncodingString.Soap11Utf16FFFE;
                case FramingEncodingType.Soap12Utf8:
                    return FramingEncodingString.Soap12Utf8;
                case FramingEncodingType.Soap12Utf16:
                    return FramingEncodingString.Soap12Utf16;
                case FramingEncodingType.Soap12Utf16FFFE:
                    return FramingEncodingString.Soap12Utf16FFFE;
                case FramingEncodingType.MTOM:
                    return FramingEncodingString.MTOM;
                case FramingEncodingType.Binary:
                    return FramingEncodingString.Binary;
                case FramingEncodingType.BinarySession:
                    return FramingEncodingString.BinarySession;
                default:
                    return "unknown" + ((int)type).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    abstract class FramingDecoder
    {
        long streamPosition;

        protected FramingDecoder()
        {
        }

        protected FramingDecoder(long streamPosition)
        {
            this.streamPosition = streamPosition;
        }

        protected abstract string CurrentStateAsString { get; }

        public long StreamPosition
        {
            get { return streamPosition; }
            set { streamPosition = value; }
        }

        protected void ValidateFramingMode(FramingMode mode)
        {
            switch (mode)
            {
                case FramingMode.Singleton:
                case FramingMode.Duplex:
                case FramingMode.Simplex:
                case FramingMode.SingletonSized:
                    break;
                default:
                    {
                        Exception exception = CreateException(new InvalidDataException(SR.Format(
                            SR.FramingModeNotSupported, mode.ToString())), FramingEncodingString.UnsupportedModeFault);
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(exception);
                    }
            }
        }

        protected void ValidateRecordType(FramingRecordType expectedType, FramingRecordType foundType)
        {
            if (foundType != expectedType)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateInvalidRecordTypeException(expectedType, foundType));
            }
        }

        // special validation for Preamble Ack for usability purposes (MB#39593)
        protected void ValidatePreambleAck(FramingRecordType foundType)
        {
            if (foundType != FramingRecordType.PreambleAck)
            {
                Exception inner = CreateInvalidRecordTypeException(FramingRecordType.PreambleAck, foundType);
                string exceptionString;
                if (((byte)foundType == 'h') || ((byte)foundType == 'H'))
                {
                    exceptionString = SR.PreambleAckIncorrectMaybeHttp;
                }
                else
                {
                    exceptionString = SR.PreambleAckIncorrect;
                }
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ProtocolException(exceptionString, inner));
            }
        }

        Exception CreateInvalidRecordTypeException(FramingRecordType expectedType, FramingRecordType foundType)
        {
            return new InvalidDataException(SR.Format(SR.FramingRecordTypeMismatch, expectedType.ToString(), foundType.ToString()));
        }

        protected void ValidateMajorVersion(int majorVersion)
        {
            if (majorVersion != FramingVersion.Major)
            {
                Exception exception = CreateException(new InvalidDataException(SR.Format(
                    SR.FramingVersionNotSupported, majorVersion)), FramingEncodingString.UnsupportedVersionFault);
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(exception);
            }
        }

        public Exception CreatePrematureEOFException()
        {
            return CreateException(new InvalidDataException(SR.FramingPrematureEOF));
        }

        protected Exception CreateException(InvalidDataException innerException, string framingFault)
        {
            Exception result = CreateException(innerException);
            FramingEncodingString.AddFaultString(result, framingFault);
            return result;
        }

        protected Exception CreateException(InvalidDataException innerException)
        {
            return new ProtocolException(SR.Format(SR.FramingError, StreamPosition, CurrentStateAsString),
                innerException);
        }
    }

    // Pattern: 
    //   Done
    class ServerModeDecoder : FramingDecoder
    {
        State currentState;
        int majorVersion;
        int minorVersion;
        FramingMode mode;

        public ServerModeDecoder()
        {
            Reset();
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            
            try
            {
                int bytesConsumed;
                switch (currentState)
                {
                    case State.ReadingVersionRecord:
                        ValidateRecordType(FramingRecordType.Version, (FramingRecordType)data[0]);
                        currentState = State.ReadingMajorVersion;
                        bytesConsumed = 1;
                        break;
                    case State.ReadingMajorVersion:
                        majorVersion = data[0];
                        ValidateMajorVersion(majorVersion);
                        currentState = State.ReadingMinorVersion;
                        bytesConsumed = 1;
                        break;
                    case State.ReadingMinorVersion:
                        minorVersion = data[0];
                        currentState = State.ReadingModeRecord;
                        bytesConsumed = 1;
                        break;
                    case State.ReadingModeRecord:
                        ValidateRecordType(FramingRecordType.Mode, (FramingRecordType)data[0]);
                        currentState = State.ReadingModeValue;
                        bytesConsumed = 1;
                        break;
                    case State.ReadingModeValue:
                        mode = (FramingMode)data[0];
                        ValidateFramingMode(mode);
                        currentState = State.Done;
                        bytesConsumed = 1;
                        break;
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }

        public void Reset()
        {
            currentState = State.ReadingVersionRecord;
        }

        internal async Task ReadModeAsync(PipeReader inputPipe)
        {
            ReadOnlySequence<byte> buffer;
            while (true)
            {
                var readResult = await inputPipe.ReadAsync();
                buffer = readResult.Buffer;
                
                while(buffer.Length > 0)
                { 
                    int bytesDecoded;
                    try
                    {
                        bytesDecoded = Decode(buffer);
                    }
                    catch (CommunicationException e)
                    {
                        // see if we need to send back a framing fault
                        string framingFault;
                        if (FramingEncodingString.TryGetFaultString(e, out framingFault))
                        {
                            // TODO: Drain the rest of the data and send a fault then close the connection
                            //byte[] drainBuffer = new byte[128];
                            //InitialServerConnectionReader.SendFault(
                            //    Connection, framingFault, drainBuffer, GetRemainingTimeout(),
                            //    MaxViaSize + MaxContentTypeSize);
                            //base.Close(GetRemainingTimeout());
                        }
                        throw;
                    }

                    if (bytesDecoded > 0)
                    {
                        buffer = buffer.Slice(bytesDecoded);
                    }

                    if (CurrentState == State.Done)
                    {
                        inputPipe.AdvanceTo(buffer.Start);
                        return;
                    }
                }

                inputPipe.AdvanceTo(buffer.End);
            }
        }

        public State CurrentState
        {
            get { return currentState; }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public FramingMode Mode
        {
            get
            {
                if (currentState != State.Done)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return mode;
            }
        }

        public int MajorVersion
        {
            get
            {
                if (currentState != State.Done)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return majorVersion;
            }
        }

        public int MinorVersion
        {
            get
            {
                if (currentState != State.Done)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return minorVersion;
            }
        }

        public enum State
        {
            ReadingVersionRecord,
            ReadingMajorVersion,
            ReadingMinorVersion,
            ReadingModeRecord,
            ReadingModeValue,
            Done,
        }
    }

    // Used for Duplex/Simplex
    // Pattern: 
    //   Start, 
    //   (UpgradeRequest, upgrade-content-type)*, 
    //   (EnvelopeStart, ReadingEnvelopeBytes*, EnvelopeEnd)*, 
    //   End
    class ServerSessionDecoder : FramingDecoder
    {
        ViaStringDecoder viaDecoder;
        StringDecoder contentTypeDecoder;
        IntDecoder sizeDecoder;
        State currentState;
        string contentType;
        int envelopeBytesNeeded;
        int envelopeSize;
        string upgrade;

        public ServerSessionDecoder(int maxViaLength, int maxContentTypeLength)
        {
            viaDecoder = new ViaStringDecoder(maxViaLength);
            contentTypeDecoder = new ContentTypeStringDecoder(maxContentTypeLength);
            sizeDecoder = new IntDecoder();
            Reset();
        }

        public State CurrentState
        {
            get { return currentState; }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public string ContentType
        {
            get
            {
                if (currentState < State.PreUpgradeStart)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return contentType;
            }
        }

        public Uri Via
        {
            get
            {
                if (currentState < State.ReadingContentTypeRecord)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return viaDecoder.ValueAsUri;
            }
        }

        public void Reset()
        {
            currentState = State.ReadingViaRecord;
        }

        public string Upgrade
        {
            get
            {
                if (currentState != State.UpgradeRequest)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return upgrade;
            }
        }

        public int EnvelopeSize
        {
            get
            {
                if (currentState < State.EnvelopeStart)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return envelopeSize;
            }
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                FramingRecordType recordType;
                switch (currentState)
                {
                    case State.ReadingViaRecord:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.Via, recordType);
                        bytesConsumed = 1;
                        viaDecoder.Reset();
                        currentState = State.ReadingViaString;
                        break;
                    case State.ReadingViaString:
                        bytesConsumed = viaDecoder.Decode(buffer);
                        if (viaDecoder.IsValueDecoded)
                        {
                            currentState = State.ReadingContentTypeRecord;
                        }
                        break;
                    case State.ReadingContentTypeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.KnownEncoding)
                        {
                            bytesConsumed = 1;
                            currentState = State.ReadingContentTypeByte;
                        }
                        else
                        {
                            ValidateRecordType(FramingRecordType.ExtensibleEncoding, recordType);
                            bytesConsumed = 1;
                            contentTypeDecoder.Reset();
                            currentState = State.ReadingContentTypeString;
                        }
                        break;
                    case State.ReadingContentTypeByte:
                        contentType = ContentTypeStringDecoder.GetString((FramingEncodingType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.PreUpgradeStart;
                        break;
                    case State.ReadingContentTypeString:
                        bytesConsumed = contentTypeDecoder.Decode(buffer);
                        if (contentTypeDecoder.IsValueDecoded)
                        {
                            currentState = State.PreUpgradeStart;
                            contentType = contentTypeDecoder.Value;
                        }
                        break;
                    case State.PreUpgradeStart:
                        bytesConsumed = 0;
                        currentState = State.ReadingUpgradeRecord;
                        break;
                    case State.ReadingUpgradeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.UpgradeRequest)
                        {
                            bytesConsumed = 1;
                            contentTypeDecoder.Reset();
                            currentState = State.ReadingUpgradeString;
                        }
                        else
                        {
                            bytesConsumed = 0;
                            currentState = State.ReadingPreambleEndRecord;
                        }
                        break;
                    case State.ReadingUpgradeString:
                        bytesConsumed = contentTypeDecoder.Decode(buffer);
                        if (contentTypeDecoder.IsValueDecoded)
                        {
                            currentState = State.UpgradeRequest;
                            upgrade = contentTypeDecoder.Value;
                        }
                        break;
                    case State.UpgradeRequest:
                        bytesConsumed = 0;
                        currentState = State.ReadingUpgradeRecord;
                        break;
                    case State.ReadingPreambleEndRecord:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.PreambleEnd, recordType);
                        bytesConsumed = 1;
                        currentState = State.Start;
                        break;
                    case State.Start:
                        bytesConsumed = 0;
                        currentState = State.ReadingEndRecord;
                        break;
                    case State.ReadingEndRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.End)
                        {
                            bytesConsumed = 1;
                            currentState = State.End;
                        }
                        else
                        {
                            bytesConsumed = 0;
                            currentState = State.ReadingEnvelopeRecord;
                        }
                        break;
                    case State.ReadingEnvelopeRecord:
                        ValidateRecordType(FramingRecordType.SizedEnvelope, (FramingRecordType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.ReadingEnvelopeSize;
                        sizeDecoder.Reset();
                        break;
                    case State.ReadingEnvelopeSize:
                        bytesConsumed = sizeDecoder.Decode(buffer);
                        if (sizeDecoder.IsValueDecoded)
                        {
                            currentState = State.EnvelopeStart;
                            envelopeSize = sizeDecoder.Value;
                            envelopeBytesNeeded = envelopeSize;
                        }
                        break;
                    case State.EnvelopeStart:
                        bytesConsumed = 0;
                        currentState = State.ReadingEnvelopeBytes;
                        break;
                    case State.ReadingEnvelopeBytes:
                        bytesConsumed = (int)buffer.Length;
                        if (bytesConsumed > envelopeBytesNeeded)
                            bytesConsumed = envelopeBytesNeeded;
                        envelopeBytesNeeded -= bytesConsumed;
                        if (envelopeBytesNeeded == 0)
                            currentState = State.EnvelopeEnd;
                        break;
                    case State.EnvelopeEnd:
                        bytesConsumed = 0;
                        currentState = State.ReadingEndRecord;
                        break;
                    case State.End:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }

        public enum State
        {
            ReadingViaRecord,
            ReadingViaString,
            ReadingContentTypeRecord,
            ReadingContentTypeString,
            ReadingContentTypeByte,
            PreUpgradeStart,
            ReadingUpgradeRecord,
            ReadingUpgradeString,
            UpgradeRequest,
            ReadingPreambleEndRecord,
            Start,
            ReadingEnvelopeRecord,
            ReadingEnvelopeSize,
            EnvelopeStart,
            ReadingEnvelopeBytes,
            EnvelopeEnd,
            ReadingEndRecord,
            End,
        }
    }

    class SingletonMessageDecoder : FramingDecoder
    {
        IntDecoder sizeDecoder;
        int chunkBytesNeeded;
        int chunkSize;
        State currentState;

        public SingletonMessageDecoder(long streamPosition)
            : base(streamPosition)
        {
            sizeDecoder = new IntDecoder();
            currentState = State.ChunkStart;
        }

        public void Reset()
        {
            currentState = State.ChunkStart;
        }

        public State CurrentState
        {
            get { return currentState; }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public int ChunkSize
        {
            get
            {
                if (currentState < State.ChunkStart)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                }

                return chunkSize;
            }
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                switch (currentState)
                {
                    case State.ReadingEnvelopeChunkSize:
                        bytesConsumed = sizeDecoder.Decode(buffer);
                        if (sizeDecoder.IsValueDecoded)
                        {
                            chunkSize = sizeDecoder.Value;
                            sizeDecoder.Reset();

                            if (chunkSize == 0)
                            {
                                currentState = State.EnvelopeEnd;
                            }
                            else
                            {
                                currentState = State.ChunkStart;
                                chunkBytesNeeded = chunkSize;
                            }
                        }
                        break;
                    case State.ChunkStart:
                        bytesConsumed = 0;
                        currentState = State.ReadingEnvelopeBytes;
                        break;
                    case State.ReadingEnvelopeBytes:
                        bytesConsumed = (int)buffer.Length;
                        if (bytesConsumed > chunkBytesNeeded)
                        {
                            bytesConsumed = chunkBytesNeeded;
                        }
                        chunkBytesNeeded -= bytesConsumed;
                        if (chunkBytesNeeded == 0)
                        {
                            currentState = State.ChunkEnd;
                        }
                        break;
                    case State.ChunkEnd:
                        bytesConsumed = 0;
                        currentState = State.ReadingEnvelopeChunkSize;
                        break;
                    case State.EnvelopeEnd:
                        ValidateRecordType(FramingRecordType.End, (FramingRecordType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.End;
                        break;
                    case State.End:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));

                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                StreamPosition += bytesConsumed;
                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }

        public enum State
        {
            ReadingEnvelopeChunkSize,
            ChunkStart,
            ReadingEnvelopeBytes,
            ChunkEnd,
            EnvelopeEnd,
            End,
        }
    }

    // Pattern: 
    //   Start, 
    //   (UpgradeRequest, upgrade-bytes)*, 
    //   EnvelopeStart,
    class ServerSingletonDecoder : FramingDecoder
    {
        ViaStringDecoder viaDecoder;
        ContentTypeStringDecoder contentTypeDecoder;
        State currentState;
        string contentType;
        string upgrade;

        public ServerSingletonDecoder(int maxViaLength, int maxContentTypeLength)
        {
            viaDecoder = new ViaStringDecoder(maxViaLength);
            contentTypeDecoder = new ContentTypeStringDecoder(maxContentTypeLength);
            Reset();
        }

        public void Reset()
        {
            currentState = State.ReadingViaRecord;
        }

        public State CurrentState
        {
            get { return currentState; }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public Uri Via
        {
            get
            {
                if (currentState < State.ReadingContentTypeRecord)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return viaDecoder.ValueAsUri;
            }
        }

        public string ContentType
        {
            get
            {
                if (currentState < State.PreUpgradeStart)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return contentType;
            }
        }

        public string Upgrade
        {
            get
            {
                if (currentState != State.UpgradeRequest)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return upgrade;
            }
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                FramingRecordType recordType;
                switch (currentState)
                {
                    case State.ReadingViaRecord:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.Via, recordType);
                        bytesConsumed = 1;
                        viaDecoder.Reset();
                        currentState = State.ReadingViaString;
                        break;
                    case State.ReadingViaString:
                        bytesConsumed = viaDecoder.Decode(buffer);
                        if (viaDecoder.IsValueDecoded)
                        {
                            currentState = State.ReadingContentTypeRecord;
                        }
                        break;
                    case State.ReadingContentTypeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.KnownEncoding)
                        {
                            bytesConsumed = 1;
                            currentState = State.ReadingContentTypeByte;
                        }
                        else
                        {
                            ValidateRecordType(FramingRecordType.ExtensibleEncoding, recordType);
                            bytesConsumed = 1;
                            contentTypeDecoder.Reset();
                            currentState = State.ReadingContentTypeString;
                        }
                        break;
                    case State.ReadingContentTypeByte:
                        contentType = ContentTypeStringDecoder.GetString((FramingEncodingType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.PreUpgradeStart;
                        break;
                    case State.ReadingContentTypeString:
                        bytesConsumed = contentTypeDecoder.Decode(buffer);
                        if (contentTypeDecoder.IsValueDecoded)
                        {
                            currentState = State.PreUpgradeStart;
                            contentType = contentTypeDecoder.Value;
                        }
                        break;
                    case State.PreUpgradeStart:
                        bytesConsumed = 0;
                        currentState = State.ReadingUpgradeRecord;
                        break;
                    case State.ReadingUpgradeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.UpgradeRequest)
                        {
                            bytesConsumed = 1;
                            contentTypeDecoder.Reset();
                            currentState = State.ReadingUpgradeString;
                        }
                        else
                        {
                            bytesConsumed = 0;
                            currentState = State.ReadingPreambleEndRecord;
                        }
                        break;
                    case State.ReadingUpgradeString:
                        bytesConsumed = contentTypeDecoder.Decode(buffer);
                        if (contentTypeDecoder.IsValueDecoded)
                        {
                            currentState = State.UpgradeRequest;
                            upgrade = contentTypeDecoder.Value;
                        }
                        break;
                    case State.UpgradeRequest:
                        bytesConsumed = 0;
                        currentState = State.ReadingUpgradeRecord;
                        break;
                    case State.ReadingPreambleEndRecord:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.PreambleEnd, recordType);
                        bytesConsumed = 1;
                        currentState = State.Start;
                        break;
                    case State.Start:
                        bytesConsumed = 0;
                        currentState = State.ReadingEnvelopeRecord;
                        break;
                    case State.ReadingEnvelopeRecord:
                        ValidateRecordType(FramingRecordType.UnsizedEnvelope, (FramingRecordType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.EnvelopeStart;
                        break;
                    case State.EnvelopeStart:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                StreamPosition += bytesConsumed;
                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }

        public enum State
        {
            ReadingViaRecord,
            ReadingViaString,
            ReadingContentTypeRecord,
            ReadingContentTypeString,
            ReadingContentTypeByte,
            PreUpgradeStart,
            ReadingUpgradeRecord,
            ReadingUpgradeString,
            UpgradeRequest,
            ReadingPreambleEndRecord,
            Start,
            ReadingEnvelopeRecord,
            EnvelopeStart,
            ReadingEnvelopeChunkSize,
            ChunkStart,
            ReadingEnvelopeChunk,
            ChunkEnd,
            End,
        }
    }

    // Pattern: 
    //   Start, 
    //   EnvelopeStart,
    class ServerSingletonSizedDecoder : FramingDecoder
    {
        ViaStringDecoder viaDecoder;
        ContentTypeStringDecoder contentTypeDecoder;
        State currentState;
        string contentType;

        public ServerSingletonSizedDecoder(long streamPosition, int maxViaLength, int maxContentTypeLength)
            : base(streamPosition)
        {
            viaDecoder = new ViaStringDecoder(maxViaLength);
            contentTypeDecoder = new ContentTypeStringDecoder(maxContentTypeLength);
            currentState = State.ReadingViaRecord;
        }

        public int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                FramingRecordType recordType;
                switch (currentState)
                {
                    case State.ReadingViaRecord:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.Via, recordType);
                        bytesConsumed = 1;
                        viaDecoder.Reset();
                        currentState = State.ReadingViaString;
                        break;
                    case State.ReadingViaString:
                        bytesConsumed = viaDecoder.Decode(buffer);
                        if (viaDecoder.IsValueDecoded)
                            currentState = State.ReadingContentTypeRecord;
                        break;
                    case State.ReadingContentTypeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.KnownEncoding)
                        {
                            bytesConsumed = 1;
                            currentState = State.ReadingContentTypeByte;
                        }
                        else
                        {
                            ValidateRecordType(FramingRecordType.ExtensibleEncoding, recordType);
                            bytesConsumed = 1;
                            contentTypeDecoder.Reset();
                            currentState = State.ReadingContentTypeString;
                        }
                        break;
                    case State.ReadingContentTypeByte:
                        contentType = ContentTypeStringDecoder.GetString((FramingEncodingType)data[0]);
                        bytesConsumed = 1;
                        currentState = State.Start;
                        break;
                    case State.ReadingContentTypeString:
                        bytesConsumed = contentTypeDecoder.Decode(buffer);
                        if (contentTypeDecoder.IsValueDecoded)
                        {
                            currentState = State.Start;
                            contentType = contentTypeDecoder.Value;
                        }
                        break;
                    case State.Start:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                StreamPosition += bytesConsumed;
                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }

        public void Reset(long streamPosition)
        {
            StreamPosition = streamPosition;
            currentState = State.ReadingViaRecord;
        }

        public State CurrentState
        {
            get { return currentState; }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public Uri Via
        {
            get
            {
                if (currentState < State.ReadingContentTypeRecord)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return viaDecoder.ValueAsUri;
            }
        }

        public string ContentType
        {
            get
            {
                if (currentState < State.Start)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return contentType;
            }
        }

        public enum State
        {
            ReadingViaRecord,
            ReadingViaString,
            ReadingContentTypeRecord,
            ReadingContentTypeString,
            ReadingContentTypeByte,
            Start,
        }
    }

    // common set of states used on the client-side.
    enum ClientFramingDecoderState
    {
        ReadingUpgradeRecord,
        ReadingUpgradeMode,
        UpgradeResponse,
        ReadingAckRecord,
        Start,
        ReadingFault,
        ReadingFaultString,
        Fault,
        ReadingEnvelopeRecord,
        ReadingEnvelopeSize,
        EnvelopeStart,
        ReadingEnvelopeBytes,
        EnvelopeEnd,
        ReadingEndRecord,
        End,
    }

    abstract class ClientFramingDecoder : FramingDecoder
    {
        ClientFramingDecoderState currentState;

        protected ClientFramingDecoder(long streamPosition)
            : base(streamPosition)
        {
            currentState = ClientFramingDecoderState.ReadingUpgradeRecord;
        }

        public ClientFramingDecoderState CurrentState
        {
            get
            {
                return currentState;
            }

            protected set
            {
                currentState = value;
            }
        }

        protected override string CurrentStateAsString
        {
            get { return currentState.ToString(); }
        }

        public abstract string Fault
        {
            get;
        }

        public abstract int Decode(ReadOnlySequence<byte> buffer);
    }

    // Pattern: 
    //   (UpgradeResponse, upgrade-bytes)*, (Ack | Fault),
    //   ((EnvelopeStart, ReadingEnvelopeBytes*, EnvelopeEnd) | Fault)*, 
    //   End
    class ClientDuplexDecoder : ClientFramingDecoder
    {
        IntDecoder sizeDecoder;
        FaultStringDecoder faultDecoder;
        int envelopeBytesNeeded;
        int envelopeSize;

        public ClientDuplexDecoder(long streamPosition)
            : base(streamPosition)
        {
            sizeDecoder = new IntDecoder();
        }

        public int EnvelopeSize
        {
            get
            {
                if (CurrentState < ClientFramingDecoderState.EnvelopeStart)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return envelopeSize;
            }
        }

        public override string Fault
        {
            get
            {
                if (CurrentState < ClientFramingDecoderState.Fault)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return faultDecoder.Value;
            }
        }

        public override int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                FramingRecordType recordType;
                switch (CurrentState)
                {
                    case ClientFramingDecoderState.ReadingUpgradeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.UpgradeResponse)
                        {
                            bytesConsumed = 1;
                            base.CurrentState = ClientFramingDecoderState.UpgradeResponse;
                        }
                        else
                        {
                            bytesConsumed = 0;
                            base.CurrentState = ClientFramingDecoderState.ReadingAckRecord;
                        }
                        break;
                    case ClientFramingDecoderState.UpgradeResponse:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingUpgradeRecord;
                        break;
                    case ClientFramingDecoderState.ReadingAckRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.Fault)
                        {
                            bytesConsumed = 1;
                            faultDecoder = new FaultStringDecoder();
                            base.CurrentState = ClientFramingDecoderState.ReadingFaultString;
                            break;
                        }
                        ValidatePreambleAck(recordType);
                        bytesConsumed = 1;
                        base.CurrentState = ClientFramingDecoderState.Start;
                        break;
                    case ClientFramingDecoderState.Start:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingEnvelopeRecord;
                        break;
                    case ClientFramingDecoderState.ReadingEnvelopeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.End)
                        {
                            bytesConsumed = 1;
                            base.CurrentState = ClientFramingDecoderState.End;
                            break;
                        }
                        else if (recordType == FramingRecordType.Fault)
                        {
                            bytesConsumed = 1;
                            faultDecoder = new FaultStringDecoder();
                            base.CurrentState = ClientFramingDecoderState.ReadingFaultString;
                            break;
                        }
                        ValidateRecordType(FramingRecordType.SizedEnvelope, recordType);
                        bytesConsumed = 1;
                        base.CurrentState = ClientFramingDecoderState.ReadingEnvelopeSize;
                        sizeDecoder.Reset();
                        break;
                    case ClientFramingDecoderState.ReadingEnvelopeSize:
                        bytesConsumed = sizeDecoder.Decode(buffer);
                        if (sizeDecoder.IsValueDecoded)
                        {
                            base.CurrentState = ClientFramingDecoderState.EnvelopeStart;
                            envelopeSize = sizeDecoder.Value;
                            envelopeBytesNeeded = envelopeSize;
                        }
                        break;
                    case ClientFramingDecoderState.EnvelopeStart:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingEnvelopeBytes;
                        break;
                    case ClientFramingDecoderState.ReadingEnvelopeBytes:
                        bytesConsumed = (int)buffer.Length;
                        if (bytesConsumed > envelopeBytesNeeded)
                            bytesConsumed = envelopeBytesNeeded;
                        envelopeBytesNeeded -= bytesConsumed;
                        if (envelopeBytesNeeded == 0)
                            base.CurrentState = ClientFramingDecoderState.EnvelopeEnd;
                        break;
                    case ClientFramingDecoderState.EnvelopeEnd:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingEnvelopeRecord;
                        break;
                    case ClientFramingDecoderState.ReadingFaultString:
                        bytesConsumed = faultDecoder.Decode(buffer);
                        if (faultDecoder.IsValueDecoded)
                        {
                            base.CurrentState = ClientFramingDecoderState.Fault;
                        }
                        break;
                    case ClientFramingDecoderState.Fault:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingEndRecord;
                        break;
                    case ClientFramingDecoderState.ReadingEndRecord:
                        ValidateRecordType(FramingRecordType.End, (FramingRecordType)data[0]);
                        bytesConsumed = 1;
                        base.CurrentState = ClientFramingDecoderState.End;
                        break;
                    case ClientFramingDecoderState.End:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                StreamPosition += bytesConsumed;
                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }
    }

    // Pattern: 
    //   (UpgradeResponse, upgrade-bytes)*, (Ack | Fault),
    //   End
    class ClientSingletonDecoder : ClientFramingDecoder
    {
        FaultStringDecoder faultDecoder;

        public ClientSingletonDecoder(long streamPosition)
            : base(streamPosition)
        {
        }

        public override string Fault
        {
            get
            {
                if (CurrentState < ClientFramingDecoderState.Fault)
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.FramingValueNotAvailable));
                return faultDecoder.Value;
            }
        }

        public override int Decode(ReadOnlySequence<byte> buffer)
        {
            DecoderHelper.ValidateSize(buffer.Length);
            var data = buffer.First.Span;
            try
            {
                int bytesConsumed;
                FramingRecordType recordType;
                switch (CurrentState)
                {
                    case ClientFramingDecoderState.ReadingUpgradeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.UpgradeResponse)
                        {
                            bytesConsumed = 1;
                            base.CurrentState = ClientFramingDecoderState.UpgradeResponse;
                        }
                        else
                        {
                            bytesConsumed = 0;
                            base.CurrentState = ClientFramingDecoderState.ReadingAckRecord;
                        }
                        break;
                    case ClientFramingDecoderState.UpgradeResponse:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingUpgradeRecord;
                        break;
                    case ClientFramingDecoderState.ReadingAckRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.Fault)
                        {
                            bytesConsumed = 1;
                            faultDecoder = new FaultStringDecoder();
                            base.CurrentState = ClientFramingDecoderState.ReadingFaultString;
                            break;
                        }
                        ValidatePreambleAck(recordType);
                        bytesConsumed = 1;
                        base.CurrentState = ClientFramingDecoderState.Start;
                        break;

                    case ClientFramingDecoderState.Start:
                        bytesConsumed = 0;
                        base.CurrentState = ClientFramingDecoderState.ReadingEnvelopeRecord;
                        break;

                    case ClientFramingDecoderState.ReadingEnvelopeRecord:
                        recordType = (FramingRecordType)data[0];
                        if (recordType == FramingRecordType.End)
                        {
                            bytesConsumed = 1;
                            base.CurrentState = ClientFramingDecoderState.End;
                            break;
                        }
                        else if (recordType == FramingRecordType.Fault)
                        {
                            bytesConsumed = 0;
                            base.CurrentState = ClientFramingDecoderState.ReadingFault;
                            break;
                        }
                        ValidateRecordType(FramingRecordType.UnsizedEnvelope, recordType);
                        bytesConsumed = 1;
                        base.CurrentState = ClientFramingDecoderState.EnvelopeStart;
                        break;

                    case ClientFramingDecoderState.EnvelopeStart:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));

                    case ClientFramingDecoderState.ReadingFault:
                        recordType = (FramingRecordType)data[0];
                        ValidateRecordType(FramingRecordType.Fault, recordType);
                        bytesConsumed = 1;
                        faultDecoder = new FaultStringDecoder();
                        base.CurrentState = ClientFramingDecoderState.ReadingFaultString;
                        break;
                    case ClientFramingDecoderState.ReadingFaultString:
                        bytesConsumed = faultDecoder.Decode(buffer);
                        if (faultDecoder.IsValueDecoded)
                        {
                            base.CurrentState = ClientFramingDecoderState.Fault;
                        }
                        break;
                    case ClientFramingDecoderState.Fault:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.FramingAtEnd)));
                    default:
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                            CreateException(new InvalidDataException(SR.InvalidDecoderStateMachine)));
                }

                StreamPosition += bytesConsumed;
                return bytesConsumed;
            }
            catch (InvalidDataException e)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(CreateException(e));
            }
        }
    }
}