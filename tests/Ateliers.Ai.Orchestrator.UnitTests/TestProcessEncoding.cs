using System.Runtime.CompilerServices;
using System.Text;

internal static class TestProcessEncoding
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
