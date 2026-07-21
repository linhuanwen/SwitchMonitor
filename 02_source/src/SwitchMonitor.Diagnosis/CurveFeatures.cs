using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 功率曲线 13 维特征 POCO。
    /// 所有数值字段在 FeatureExtractor 中已通过 Math.Round 保留精度：
    /// DurationSec 保留 2 位小数，其余保留 3 位。
    /// </summary>
    public class CurveFeatures
    {
        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;

        /// <summary>原始功率采样值序列（kW），供 P1 逐点对比使用。可空。</summary>
        public List<double> RawValues;

        /// <summary>原始采样点数</summary>
        public int SampleCount;

        /// <summary>是否打满录制窗口（n ≥ 780）</summary>
        public bool IsFullWindow;

        /// <summary>曲线是否有效（n > 0 且峰值 > 0.01 kW）</summary>
        public bool IsValid;

        /// <summary>有效动作终点下标（去尾部零填充）</summary>
        public int ActiveEnd;

        /// <summary>动作时长 (activeEnd + 1) × 0.04，秒，保留 2 位小数</summary>
        public double DurationSec;

        /// <summary>启动尖峰最大值（前 15 点内搜索），kW</summary>
        public double SpikePeak;

        /// <summary>启动尖峰所在下标（多个相同取第一个）</summary>
        public int SpikeIndex;

        /// <summary>解锁终点下标（物理边界：最后锁钩落下位置）</summary>
        public int UnlockEnd;

        /// <summary>锁闭起点下标（物理边界：密贴拐点位置）</summary>
        public int LockStart;

        /// <summary>解锁段均值 [spikeIndex+2, unlockEnd]，kW</summary>
        public double UnlockMean;

        /// <summary>转换段均值 [unlockEnd+1, lockStart)，kW</summary>
        public double ConvMean;

        /// <summary>转换段最大值，kW</summary>
        public double ConvMax;

        /// <summary>台阶比 = 转换段后1/3均值 / 前1/3均值</summary>
        public double StepRatio;

        /// <summary>锁闭段均值 [lockStart, lockEnd]，kW</summary>
        public double LockMean;

        /// <summary>缓放段均值 [lockEnd+1, activeEnd-2)，kW</summary>
        public double TailMean;
    }
}
