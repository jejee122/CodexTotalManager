namespace ExtensionTestPlugin;

public sealed class Marker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--crash", StringComparer.Ordinal)) return 7;
        Console.WriteLine("EXTENSION_TEST_PLUGIN_OK");
        Console.WriteLine("ARGS=" + string.Join('|', args));
        Console.WriteLine("ID=" + Environment.GetEnvironmentVariable("CMM_EXTENSION_ID"));
        Console.WriteLine("DATA=" + Environment.GetEnvironmentVariable("CMM_EXTENSION_DATA_DIR"));
        Console.WriteLine("SENSITIVE=" + (Environment.GetEnvironmentVariable("CMM_TEST_SENSITIVE") ?? "<absent>"));
        return 0;
    }
}
