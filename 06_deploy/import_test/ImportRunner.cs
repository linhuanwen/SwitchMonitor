using System;
using System.IO;
using SwitchMonitor.Data;

class ImportRunner
{
    static void Main()
    {
        Console.WriteLine("=== CSV Data Import Tool ===");

        string configPath = "config.json";
        Console.WriteLine("Config: " + Path.GetFullPath(configPath));

        var result = ConfigManager.LoadConfigWithStatus(configPath);
        AppConfig config = result.Item1;
        if (result.Item2) Console.WriteLine("[WARN] Config fallback used");

        // Resolve parsed data dir relative to current directory
        string parsedDir = Path.GetFullPath(config.ParsedDataDir);
        Console.WriteLine("Output: " + parsedDir);

        var indexMgr = new IndexManager(parsedDir);
        indexMgr.Initialize();

        var pipeline = new DataPipeline(config, indexMgr);
        pipeline.OnProgress += (msg, pct) => Console.WriteLine("  [{0}%] {1}", pct, msg);

        try
        {
            pipeline.ImportAll();
            Console.WriteLine();
            Console.WriteLine("DONE! {0} events imported.", pipeline.TotalEventsImported);
            Console.WriteLine("Data saved to: " + parsedDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }
}
