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

using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using Shouldly;

namespace ServiceModel.Grpc.DesignTime.Generator;

[TestFixture]
public class SyntaxFactoryExtensionsTest
{
    [Test]
    [TestCaseSource(nameof(GetFullNameCases))]
    public void GetFullName(ClassDeclarationSyntax declaration, string expected)
    {
        declaration.GetFullName().ShouldBe(expected);
    }

    private static IEnumerable<TestCaseData> GetFullNameCases()
    {
        yield return new TestCaseData(SyntaxFactory.ClassDeclaration("Class"), "Class") { TestName = "Class" };

        var declaration = SyntaxFactory
            .NamespaceDeclaration(SyntaxFactory.ParseName("Namespace"))
            .AddMembers(SyntaxFactory.ClassDeclaration("Class"))
            .Members[0];
        yield return new TestCaseData(declaration, "Namespace.Class") { TestName = "Namespace.Class" };

        declaration = SyntaxFactory.ClassDeclaration("Class")
            .AddMembers(SyntaxFactory.ClassDeclaration("NestedClass"))
            .Members[0];
        yield return new TestCaseData(declaration, "Class.NestedClass") { TestName = "Class.NestedClass" };

        declaration = SyntaxFactory
            .FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("Namespace"))
            .AddMembers(
                SyntaxFactory.ClassDeclaration("Class")
                    .AddMembers(SyntaxFactory.ClassDeclaration("NestedClass")))
            .Members[0];
        yield return new TestCaseData(((ClassDeclarationSyntax)declaration).Members[0], "Namespace.Class.NestedClass") { TestName = "Namespace.Class.NestedClass" };
    }
}