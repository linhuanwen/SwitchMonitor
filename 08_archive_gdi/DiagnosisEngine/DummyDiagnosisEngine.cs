using System;
using System.Collections.Generic;
using SwitchMonitor.Common;
using SwitchMonitor.Diagnosis;

namespace DiagnosisEngine
{
    /// <summary>
    /// 占位诊断引擎实现。
    /// 此实现不执行真实诊断规则，仅返回"正常"结果。
    /// 生产环境中应替换为完整的诊断引擎 DLL。
    /// </summary>
    public class DummyDiagnosisEngine : IDiagnosisEngine
    {
        private bool _initialized;
        private string _rulesPath;

        /// <summary>
        /// 初始化诊断引擎
        /// </summary>
        public void Initialize(string rulesPath)
        {
            _rulesPath = rulesPath;
            _initialized = true;
        }

        /// <summary>
        /// 执行诊断（占位实现：总是返回正常）
        /// </summary>
        public List<DiagnosisResult> Diagnose(SwitchActionData data)
        {
            if (!_initialized)
                throw new InvalidOperationException("诊断引擎未初始化，请先调用 Initialize()");

            if (data == null)
                throw new ArgumentNullException("data");

            // 占位：始终返回"正常"结论
            return new List<DiagnosisResult>
            {
                new DiagnosisResult
                {
                    RuleName = "占位诊断",
                    Level = DiagnosisLevel.Normal,
                    Description = string.Format(
                        "诊断引擎尚未配置实际规则。道岔={0}, 方向={1}, 采样点数={2}",
                        data.SwitchId, data.Direction,
                        data.Samples != null ? data.Samples.Count : 0),
                    AbnormalValue = null,
                    ReferenceValue = null
                }
            };
        }
    }
}
