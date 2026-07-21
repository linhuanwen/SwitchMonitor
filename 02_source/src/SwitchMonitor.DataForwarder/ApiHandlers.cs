using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Storage;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// HTTP API 处理器。处理 /api/status 和 /api/events 请求。
    /// </summary>
    public static class ApiHandlers
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        /// <summary>
        /// GET /api/status → {"stationId":"SSB","stationName":"三水北站","status":"ok","lastTimestamp":...,"dbSizeMB":320}
        /// </summary>
        public static void HandleStatus(HttpListenerContext ctx, StorageManager storage, ForwarderConfig config)
        {
            long lastTs = storage.GetMaxTimestamp();
            long dbSize = storage.GetFileSize();
            double dbSizeMB = Math.Round(dbSize / (1024.0 * 1024.0), 1);

            var response = new Dictionary<string, object>
            {
                { "stationId", config.StationId },
                { "stationName", config.StationName },
                { "status", "ok" },
                { "lastTimestamp", lastTs },
                { "dbSizeMB", dbSizeMB }
            };

            WriteJsonResponse(ctx, Serializer.Serialize(response), compress: false);
        }

        /// <summary>
        /// GET /api/events?since={timestamp} → gzip 压缩的事件列表。
        /// </summary>
        public static void HandleEvents(HttpListenerContext ctx, StorageManager storage, ForwarderConfig config)
        {
            // 解析 since 参数
            long since = 0;
            string query = ctx.Request.Url.Query;
            if (!string.IsNullOrEmpty(query))
            {
                // 手动解析 ?since=xxx 或 &since=xxx
                foreach (string part in query.TrimStart('?').Split('&'))
                {
                    string[] kv = part.Split('=');
                    if (kv.Length == 2 && kv[0].ToLowerInvariant() == "since")
                    {
                        long.TryParse(kv[1], out since);
                        break;
                    }
                }
            }

            List<EventRecord> events = storage.GetEventsSince(since);

            // 构建响应 JSON，BLOB 转为 Base64（接收端可解码）
            var eventDicts = new List<Dictionary<string, object>>();
            foreach (var rec in events)
            {
                eventDicts.Add(EventToDict(rec));
            }

            var response = new Dictionary<string, object>
            {
                { "stationId", config.StationId },
                { "since", since },
                { "count", events.Count },
                { "events", eventDicts }
            };

            string json = Serializer.Serialize(response);
            WriteJsonResponse(ctx, json, compress: true);
        }

        /// <summary>
        /// EventRecord → 可序列化字典，BLOB 字段转 Base64 字符串。
        /// </summary>
        public static Dictionary<string, object> EventToDict(EventRecord rec)
        {
            var dict = new Dictionary<string, object>
            {
                { "switchId", rec.SwitchId },
                { "timestamp", rec.Timestamp },
                { "dateTimeStr", rec.DateTimeStr },
                { "direction", rec.Direction ?? "未知" },
                { "durationSec", rec.DurationSec },
                { "sampleInterval", rec.SampleInterval },
                { "sampleCount", rec.SampleCount }
            };

            // BLOB → Base64（兼容 JSON 传输）
            if (rec.CurrentABlob != null) dict["currentA"] = Convert.ToBase64String(rec.CurrentABlob);
            if (rec.CurrentBBlob != null) dict["currentB"] = Convert.ToBase64String(rec.CurrentBBlob);
            if (rec.CurrentCBlob != null) dict["currentC"] = Convert.ToBase64String(rec.CurrentCBlob);
            if (rec.PowerBlob != null) dict["power"] = Convert.ToBase64String(rec.PowerBlob);

            // 诊断结果
            if (!string.IsNullOrEmpty(rec.DiagJson))
            {
                try { dict["diagnosis"] = Serializer.Deserialize<object>(rec.DiagJson); }
                catch { dict["diagnosis"] = rec.DiagJson; }
            }

            return dict;
        }

        /// <summary>
        /// 写入 JSON 响应。compress=true 时使用 gzip。
        /// </summary>
        private static void WriteJsonResponse(HttpListenerContext ctx, string json, bool compress)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);

            if (compress)
            {
                ctx.Response.AddHeader("Content-Encoding", "gzip");
                using (var ms = new MemoryStream())
                {
                    using (var gzip = new GZipStream(ms, CompressionMode.Compress, true))
                    {
                        gzip.Write(data, 0, data.Length);
                    }
                    data = ms.ToArray();
                }
            }

            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
        }
    }
}
