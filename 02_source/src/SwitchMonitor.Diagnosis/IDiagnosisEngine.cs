using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 诊断引擎接口。
    /// UI 和管道通过此接口调用诊断，实现类在 D3 提供。
    /// </summary>
    public interface IDiagnosisEngine
    {
        /// <summary>
        /// 初始化引擎：加载 thresholds.json + baselines.json
        /// </summary>
        /// <param name="rulesDir">Rules 目录路径</param>
        void Initialize(string rulesDir);

        /// <summary>
        /// 对一次道岔动作进行诊断
        /// </summary>
        /// <param name="switchId">道岔标识（如 "1-J"）</param>
        /// <param name="features">已提取的曲线特征</param>
        /// <param name="direction">动作方向（"定位→反位" 或 "反位→定位"），用于选择对应方向的基线</param>
        /// <returns>诊断结论列表（可能为空 = 正常）</returns>
        List<DiagnosisResult> Diagnose(string switchId, CurveFeatures features, string direction = null);
    }
}
