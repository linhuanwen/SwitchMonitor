using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 阈值输入验证器。
    /// 验证用户输入的阈值是否在合法范围内（0.0 ~ 999.9）。
    /// </summary>
    public static class ThresholdValidator
    {
        /// <summary>阈值最小值（含）</summary>
        public const float MinValue = 0.0f;

        /// <summary>阈值最大值（含）</summary>
        public const float MaxValue = 999.9f;

        /// <summary>
        /// 验证报警上限值字符串是否合法。
        /// </summary>
        /// <param name="input">用户输入的文本</param>
        /// <returns>null 表示合法；否则返回错误消息字符串</returns>
        public static string ValidateUpperLimit(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "请输入阈值数值";

            float value;
            if (!float.TryParse(input.Trim(), out value))
                return string.Format("「{0}」不是有效的数字", input.Trim());

            if (value < MinValue)
                return string.Format("阈值不能为负数（当前值: {0}）", value);

            if (value > MaxValue)
                return string.Format("阈值不能超过 {0}（当前值: {1}）", MaxValue, value);

            return null; // 合法
        }

        /// <summary>
        /// 验证阈值浮点数值是否在合法范围内。
        /// </summary>
        /// <param name="value">阈值数值</param>
        /// <returns>null 表示合法；否则返回错误消息字符串</returns>
        public static string ValidateUpperLimit(float value)
        {
            if (value < MinValue)
                return string.Format("阈值不能为负数（当前值: {0}）", value);

            if (value > MaxValue)
                return string.Format("阈值不能超过 {0}（当前值: {1}）", MaxValue, value);

            return null; // 合法
        }
    }
}
