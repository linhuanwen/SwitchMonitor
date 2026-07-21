using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// site.json 读写。
    ///
    /// site.json 是站点目录下的最小元数据文件：
    /// {
    ///   "name": "三水北站",
    ///   "switchType": "ZYJ7",
    ///   "switchGroups": [                          // 可选 — 无 DC.ini 时的手动指定
    ///     {"id": "1-J", "label": "1-J", "dataFileIndex": 0}
    ///   ]
    /// }
    /// </summary>
    public static class SiteJsonReader
    {
        /// <summary>
        /// 从站点目录读取 site.json
        /// </summary>
        /// <param name="stationDir">站点目录路径</param>
        /// <returns>成功返回 StationManifest（Id/Name/SwitchType/SwitchGroups 已填充），失败返回 null</returns>
        public static StationManifest Read(string stationDir)
        {
            string jsonPath = Path.Combine(stationDir, "site.json");
            if (!File.Exists(jsonPath))
                return null;

            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var dict = serializer.Deserialize<Dictionary<string, object>>(json);

                if (dict == null)
                    return null;

                string dirName = Path.GetFileName(stationDir);

                var manifest = new StationManifest
                {
                    Id = dirName,
                    Name = GetStringValue(dict, "name") ?? dirName,
                    DataFormat = GetStringValue(dict, "dataFormat") ?? "",
                    SwitchType = GetStringValue(dict, "switchType") ?? "",
                    VendorType = GetStringValue(dict, "vendorType") ?? "",
                    MachinesPerSwitch = GetIntValue(dict, "machinesPerSwitch"),
                    SwitchCount = GetIntValue(dict, "switchCount"),
                    DataSourceDir = stationDir,
                    ParsedDataDir = ".\\parsed_data\\" + dirName
                };

                // 解析手动指定的 switchGroups（无 DC.ini 时的回退）
                object groupsObj;
                if (dict.TryGetValue("switchGroups", out groupsObj) && groupsObj is object[])
                {
                    var groupsArray = (object[])groupsObj;
                    foreach (var item in groupsArray)
                    {
                        var gDict = item as Dictionary<string, object>;
                        if (gDict == null) continue;

                        manifest.ManualSwitchGroups.Add(new SwitchGroupDef
                        {
                            Id = GetStringValue(gDict, "id") ?? "",
                            Label = GetStringValue(gDict, "label") ?? "",
                            DataFileIndex = GetIntValue(gDict, "dataFileIndex"),
                            SwitchType = GetStringValue(gDict, "switchType") ?? ""
                        });
                    }
                }

                return manifest;
            }
            catch
            {
                return null;
            }
        }

        private static string GetStringValue(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
                return val.ToString();
            return null;
        }

        private static int GetIntValue(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict.TryGetValue(key, out val) && val != null)
            {
                int result;
                if (int.TryParse(val.ToString(), out result))
                    return result;
            }
            return 0;
        }
    }
}
