using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// 站点数据模块统一入口。
    /// 提供：站点扫描 → switchGroups 推导 → 供上层消费。
    ///
    /// 本模块不依赖 SwitchMonitor.Data，返回的都是 Station 模块自有类型。
    /// 类型转换由 SwitchMonitor.Data 层的调用方负责。
    /// </summary>
    public static class StationManager
    {
        /// <summary>
        /// 发现所有站点（扫描文件系统 + DC.ini 解析）
        /// </summary>
        /// <param name="rawDataDir">03_raw_data/ 目录路径（绝对或相对）</param>
        /// <returns>发现的站点清单列表</returns>
        public static List<StationManifest> DiscoverStations(string rawDataDir)
        {
            if (string.IsNullOrEmpty(rawDataDir))
                return new List<StationManifest>();

            // 相对路径 → 转绝对
            if (!Path.IsPathRooted(rawDataDir))
                rawDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rawDataDir);

            return StationScanner.Scan(rawDataDir);
        }

        /// <summary>
        /// 单个站点目录的 switchGroups 解析。
        /// 优先级：DC.ini → site.json switchGroups → 空
        /// </summary>
        public static List<SwitchGroupDef> ResolveSwitchGroups(string stationDir)
        {
            if (string.IsNullOrEmpty(stationDir) || !Directory.Exists(stationDir))
                return new List<SwitchGroupDef>();

            // 1. 尝试 DC.ini
            string dcIniPath = Path.Combine(stationDir, "DC.ini");
            if (File.Exists(dcIniPath))
            {
                var groups = DcIniParser.ParseAndDerive(dcIniPath);
                if (groups.Count > 0)
                    return groups;
            }

            // 2. 回退 site.json
            var manifest = SiteJsonReader.Read(stationDir);
            if (manifest != null && manifest.ManualSwitchGroups.Count > 0)
                return manifest.ManualSwitchGroups;

            return new List<SwitchGroupDef>();
        }

        /// <summary>
        /// 将绝对路径转为相对于 AppDomain.BaseDirectory 的路径。
        /// 供上层（Data/UI）在生成配置时使用。
        /// </summary>
        public static string ToRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return absolutePath;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 规范化路径分隔符
            string normAbs = absolutePath.Replace('/', '\\').TrimEnd('\\');
            string normBase = baseDir.Replace('/', '\\').TrimEnd('\\');

            if (normAbs.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
            {
                string rel = normAbs.Substring(normBase.Length).TrimStart('\\');
                return ".\\" + rel;
            }

            return absolutePath;
        }
    }
}
