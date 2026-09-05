using UnityEngine;

public interface ILabelUpdatable
{
	void InitLabelUpdater(GameObject target);
	void UpdateLabels();
}

public class LabelUpdaterHandler : ILabelUpdatable
{
	private TextLabelUpdater textLabelUpdater = new();

	public void InitLabelUpdater(GameObject target)
	{
		textLabelUpdater.Initialize(target);
	}

	public void UpdateLabels()
	{
		textLabelUpdater.UpdateLabels();
	}
}
