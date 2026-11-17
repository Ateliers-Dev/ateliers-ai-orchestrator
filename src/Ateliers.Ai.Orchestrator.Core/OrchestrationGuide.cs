using System.Reflection;
using System.Text;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>Coreが管理する単一のオーケストレーションガイドを提供します。</summary>
public static class OrchestrationGuide
{
    /// <summary>埋め込みMarkdownガイドを返します。</summary>
    public static string GetText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ateliers.Ai.Orchestrator.Core.OrchestrationGuide.md")
            ?? throw new InvalidOperationException("オーケストレーションガイドがCore assemblyにありません。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
