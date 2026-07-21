using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 三相电流曲线 20 维特征 POCO。
    /// 分相段统计（6维×3相）+ 三相汇总（2维）= 20 个基线标量。
    /// 所有数值字段在 CurrentFeatureExtractor 中通过 Math.Round 保留精度。
    /// </summary>
    public class CurrentFeatures
    {
        // ── 三相独立特征（每相 6 维 × 3 = 18 维）──

        // A 相
        /// <summary>启动尖峰电流峰值 (A)</summary>
        public double SpikePeakA;
        /// <summary>尖峰所在采样点下标</summary>
        public int SpikeIndexA;
        /// <summary>解锁终点下标（物理边界）</summary>
        public int UnlockEndA;
        /// <summary>锁闭起点下标（物理边界：密贴拐点）</summary>
        public int LockStartA;
        /// <summary>解锁段均值 (A)</summary>
        public double UnlockMeanA;
        /// <summary>转换段均值 (A)</summary>
        public double ConvMeanA;
        /// <summary>锁闭段均值 (A)</summary>
        public double LockMeanA;
        /// <summary>缓放段/尾部平台均值 (A)</summary>
        public double TailMeanA;

        // B 相
        public double SpikePeakB;
        public int SpikeIndexB;
        public int UnlockEndB;
        public int LockStartB;
        public double UnlockMeanB;
        public double ConvMeanB;
        public double LockMeanB;
        public double TailMeanB;

        // C 相
        public double SpikePeakC;
        public int SpikeIndexC;
        public int UnlockEndC;
        public int LockStartC;
        public double UnlockMeanC;
        public double ConvMeanC;
        public double LockMeanC;
        public double TailMeanC;

        // ── 三相汇总（2 维）──
        /// <summary>动作时长（秒，取三相 activeEnd 的最大值 ×0.04）</summary>
        public double DurationSec;
        /// <summary>三相间最大不平衡度</summary>
        public double MaxUnbalanceRatio;

        // ── 元数据 ──
        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;
        /// <summary>A 相原始采样值（供未来 P1 逐点对比）</summary>
        public List<double> RawValuesA;
        /// <summary>B 相原始采样值</summary>
        public List<double> RawValuesB;
        /// <summary>C 相原始采样值</summary>
        public List<double> RawValuesC;
        /// <summary>原始采样点数</summary>
        public int SampleCount;
        /// <summary>是否有效（三相都非空且有数据）</summary>
        public bool IsValid;
        /// <summary>是否打满录制窗口（n ≥ 780）</summary>
        public bool IsFullWindow;
        /// <summary>有效动作终点下标（取三相最大值）</summary>
        public int ActiveEnd;

        public CurrentFeatures()
        {
            RawValuesA = new List<double>();
            RawValuesB = new List<double>();
            RawValuesC = new List<double>();
        }
    }
}
