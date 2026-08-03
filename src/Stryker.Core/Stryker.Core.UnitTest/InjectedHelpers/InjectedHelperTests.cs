using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Core.InjectedHelpers;

namespace Stryker.Core.UnitTest.InjectedHelpers;

[TestClass]
public class InjectedHelperTests : TestBase
{
    [TestMethod]
    public void MutantControl_ShouldCacheFileActivationBetweenRequestRefreshes()
    {
        var codeInjection = new CodeInjection();
        var source = codeInjection.MutantHelpers.Single(helper => helper.Key.EndsWith("MutantControl.cs", StringComparison.Ordinal)).Value;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var control = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "MutantControl");

        control.Members.OfType<FieldDeclarationSyntax>()
            .ShouldContain(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "_fileMutantValueCached"));

        var refresh = control.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "RefreshActiveMutantFromFile");
        refresh.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)).ShouldBeTrue();
        refresh.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)).ShouldBeTrue();
        refresh.ToString().ShouldContain("_fileMutantValueCached = true");

        var isActive = control.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "IsActive");
        isActive.ToString().ShouldContain("if (!_fileMutantValueCached)");
    }

    [TestMethod]
    [DataRow(LanguageVersion.CSharp2)]
    [DataRow(LanguageVersion.CSharp3)]
    [DataRow(LanguageVersion.CSharp4)]
    [DataRow(LanguageVersion.CSharp5)]
    [DataRow(LanguageVersion.CSharp6)]
    [DataRow(LanguageVersion.CSharp7)]
    [DataRow(LanguageVersion.CSharp7_1)]
    [DataRow(LanguageVersion.CSharp7_2)]
    [DataRow(LanguageVersion.CSharp7_3)]
    [DataRow(LanguageVersion.CSharp8)]
    [DataRow(LanguageVersion.Default)]
    [DataRow(LanguageVersion.Latest)]
    [DataRow(LanguageVersion.LatestMajor)]
    [DataRow(LanguageVersion.Preview)]
    public void InjectHelpers_ShouldCompile_ForAllLanguageVersions(LanguageVersion version)
    {
        // MutantControl maps the mutant-id file via MemoryMappedFile for the MTP runner; touch the type
        // so its defining assembly is loaded before the snapshot below and can be referenced.
        _ = typeof(System.IO.MemoryMappedFiles.MemoryMappedFile);

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        var needed = new[] { ".CoreLib", ".Runtime", "System.IO.Pipes", "System.IO.MemoryMappedFiles", ".Collections", ".Console" };
        var references = new List<MetadataReference>();
        foreach (var assembly in assemblies)
        {
            if (needed.Any(x => assembly.FullName.Contains(x)))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var syntaxes = new List<SyntaxTree>();
        var codeInjection = new CodeInjection();

        foreach (var helper in codeInjection.MutantHelpers)
        {
            syntaxes.Add(CSharpSyntaxTree.ParseText(helper.Value, new CSharpParseOptions(languageVersion: version),
                helper.Key));
        }

        var compilation = CSharpCompilation.Create("dummy.dll",
            syntaxes,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: references);

        compilation.GetDiagnostics().ShouldNotContain(diag => diag.Severity == DiagnosticSeverity.Error,
            $"errors :{string.Join(Environment.NewLine, compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Select(diag => $"{diag.Id}: '{diag.GetMessage()}' at {diag.Location.SourceTree.FilePath}, {diag.Location.GetLineSpan().StartLinePosition.Line + 1}:{diag.Location.GetLineSpan().StartLinePosition.Character}"))}");
    }

    [TestMethod]
    [DataRow(LanguageVersion.CSharp8)]
    [DataRow(LanguageVersion.CSharp9)]
    [DataRow(LanguageVersion.CSharp10)]
    [DataRow(LanguageVersion.CSharp11)]
    [DataRow(LanguageVersion.CSharp12)]
    [DataRow(LanguageVersion.Default)]
    [DataRow(LanguageVersion.Latest)]
    [DataRow(LanguageVersion.LatestMajor)]
    [DataRow(LanguageVersion.Preview)]
    public void InjectHelpers_ShouldCompile_ForAllLanguageVersionsWithNullableOptions(LanguageVersion version)
    {
        // MutantControl maps the mutant-id file via MemoryMappedFile for the MTP runner; touch the type
        // so its defining assembly is loaded before the snapshot below and can be referenced.
        _ = typeof(System.IO.MemoryMappedFiles.MemoryMappedFile);

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        var needed = new[] { ".CoreLib", ".Runtime", "System.IO.Pipes", "System.IO.MemoryMappedFiles", ".Collections", ".Console" };
        var references = new List<MetadataReference>();
        var hack = new NamedPipeClientStream("test");
        foreach (var assembly in assemblies)
        {
            if (needed.Any(x => assembly.FullName.Contains(x)))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var syntaxes = new List<SyntaxTree>();
        var codeInjection = new CodeInjection();

        foreach (var helper in codeInjection.MutantHelpers)
        {
            syntaxes.Add(CSharpSyntaxTree.ParseText(helper.Value, new CSharpParseOptions(languageVersion: version),
                helper.Key));
        }

        var compilation = CSharpCompilation.Create("dummy.dll",
            syntaxes,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error),
            references: references);

        compilation.GetDiagnostics().ShouldNotContain(diag => diag.Severity == DiagnosticSeverity.Error,
            $"errors :{string.Join(Environment.NewLine, compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Select(diag => $"{diag.Id}: '{diag.GetMessage()}' at {diag.Location.SourceTree.FilePath}, {diag.Location.GetLineSpan().StartLinePosition.Line + 1}:{diag.Location.GetLineSpan().StartLinePosition.Character}"))}");
    }
}
