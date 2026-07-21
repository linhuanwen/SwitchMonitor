using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// 站点文件系统扫描器 — 从 03_raw_data/ 自动发现所有站点目录。
    ///
    /// 站点识别规则：
    ///   1. 目录下存在 site.json → 直接识别为站点
    ///   2. 目录下存在 SwitchCurve(*).csv → 识别为站点（无 site.json 时用目录名作为站名）
    ///   3. 目录下存在 DC.ini → 识别为站点
    ///
    /// 跳过规则：
    ///   - 以 "Station_" 开头的目录（CSM 配置备份）
    ///   - 以 "本地" 开头的目录（接收目录）
    ///   - "Rules" 目录
    ///   - 以 "." 开头的隐藏目录
    /// </summary>
    public static class StationScanner
    {
        /// <summary>
        /// 扫描 rawDataDir 下的所有子目录，返回发现的站点清单
        /// </summary>
        public static List<StationManifest> Scan(string rawDataDir)
        {
            var stations = new List<StationManifest>();

            if (!Directory.Exists(rawDataDir))
                return stations;

            // 1. 先检查 rawDataDir 本身是否就是站点目录（兼容旧布局）
            var selfManifest = TryIdentifyStation(rawDataDir);
            if (selfManifest != null)
            {
                stations.Add(selfManifest);
                return stations; // 如果自身就是站点，不再扫描子目录
            }

            // 2. 扫描子目录
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(rawDataDir);
            }
            catch
            {
                return stations;
            }

            foreach (string subDir in subDirs)
            {
                string dirName = Path.GetFileName(subDir);

                // 跳过明显不是站点的目录
                if (ShouldSkip(dirName))
                    continue;

                var manifest = TryIdentifyStation(subDir);
                if (manifest != null)
                    stations.Add(manifest);
            }

            // 按名称排序
            stations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            return stations;
        }

        /// <summary>
        /// 尝试将一个目录识别为站点
        /// </summary>
        private static StationManifest TryIdentifyStation(string dirPath)
        {
            string dirName = Path.GetFileName(dirPath);

            // 先尝试读 site.json
            var manifest = SiteJsonReader.Read(dirPath);

            // 检查 DC.ini
            string dcIniPath = Path.Combine(dirPath, "DC.ini");
            bool hasDcIni = File.Exists(dcIniPath);

            // 统计 CSV 文件
            int csvCount = CountCsvFiles(dirPath);

            // 如果没有 site.json，也没有数据文件 → 不是站点
            if (manifest == null && !hasDcIni && csvCount == 0)
                return null;

            // 没有 site.json 但有数据 → 创建最小清单
            if (manifest == null)
            {
                manifest = new StationManifest
                {
                    Id = dirName,
                    Name = dirName,       // 用目录名作为显示名
                    SwitchType = "",
                    DataSourceDir = dirPath,
                    ParsedDataDir = ".\\parsed_data\\" + dirName
                };
            }

            manifest.HasDcIni = hasDcIni;
            manifest.CsvFileCount = csvCount;
            manifest.DataSourceDir = dirPath;

            // 推导 switchGroups：DC.ini 优先，site.json 手动指定兜底
            if (hasDcIni)
            {
                var derived = DcIniParser.ParseAndDerive(dcIniPath);
                if (derived.Count > 0)
                {
                    manifest.SwitchGroups = derived;
                }
                else if (manifest.ManualSwitchGroups.Count > 0)
                {
                    manifest.SwitchGroups = manifest.ManualSwitchGroups;
                }
            }
            else if (manifest.ManualSwitchGroups.Count > 0)
            {
                manifest.SwitchGroups = manifest.ManualSwitchGroups;
            }

            return manifest;
        }

        /// <summary>
        /// 统计目录中的 SwitchCurve(*).csv 文件数
        /// </summary>
        private static int CountCsvFiles(string dirPath)
        {
            try
            {
                var files = Directory.GetFiles(dirPath, "SwitchCurve(*).csv", SearchOption.TopDirectoryOnly);
                return files.Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 判断目录名是否应跳过
        /// </summary>
        private static bool ShouldSkip(string dirName)
        {
            if (string.IsNullOrEmpty(dirName))
                return true;
            if (dirName.StartsWith("."))
                return true;
            if (dirName.StartsWith("Station_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (dirName.StartsWith("本地", StringComparison.Ordinal))
                return true;
            if (dirName == "Rules")
                return true;
            if (dirName.EndsWith("_csv", StringComparison.OrdinalIgnoreCase))
                return true; // 旧的 CSV 输出目录
            if (dirName.StartsWith("config_", StringComparison.OrdinalIgnoreCase))
                return true; // 旧的配置输出

            return false;
        }
    }
}
