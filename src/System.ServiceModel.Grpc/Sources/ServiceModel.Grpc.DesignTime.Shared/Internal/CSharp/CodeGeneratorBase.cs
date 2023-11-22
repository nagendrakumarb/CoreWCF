﻿// <copyright>
// Copyright 2020-2022 Max Ieremenko
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ServiceModel.Grpc.DesignTime.Generator.Internal.CSharp;

internal abstract class CodeGeneratorBase
{
    protected CodeStringBuilder Output { get; private set; } = null!;

    public void GenerateMemberDeclaration(CodeStringBuilder output)
    {
        Output = output;
        Generate();
    }

    public abstract string GetGeneratedMemberName();

    protected abstract void Generate();

    protected void WriteMetadata()
    {
        Output
            .AppendAttribute(typeof(GeneratedCodeAttribute), "\"ServiceModel.Grpc\"", "\"" + GetType().Assembly.GetName().Version.ToString(3) + "\"")
            .AppendAttribute(typeof(CompilerGeneratedAttribute))
            .AppendAttribute(typeof(ExcludeFromCodeCoverageAttribute))
            .AppendAttribute(typeof(ObfuscationAttribute), "Exclude = true");
    }
}