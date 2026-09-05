using System.Collections.Generic;

public class StageDropInfoData
{
    /// <summary>
    /// 드롭 ID
    /// </summary>
    public List<int> dropID = new List<int>();
    /// <summary>
    /// 드롭 확률
    /// </summary>
    public List<float> dropPer = new List<float>();
    /// <summary>
    /// 드롭 맥스 카운트
    /// </summary>
    public List<int> dropMaxCount = new List<int>();
}

namespace STAGE
{
    public partial class StageDrop
	{
		public static Dictionary<int, StageDrop> DropItemIndexMap = new();

		static partial void OnAfterLoad()
		{
            DropItemIndexMap.Clear();

            foreach (var info in STAGE.StageDrop.StageDropList)
            {
                DropItemIndexMap.Add(info.StageIndex, info);
            }
		}
	}
}