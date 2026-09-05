using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Define;

public class UI_Pool : MonoBehaviour
{
	private static Dictionary<eUIPoolObject, Stack<PoolingObject>> pool = new Dictionary<eUIPoolObject, Stack<PoolingObject>>();
	private static Dictionary<string, PoolingObject> prefabs = new Dictionary<string, PoolingObject>();

	public static void InitPool()
	{
		prefabs = Resources.LoadAll<PoolingObject>("Prefabs/UI/Pool").ToDictionary(x => x.name);
		pool.Clear();
		var enumValue = Enum.GetValues(typeof(eUIPoolObject));
		foreach (eUIPoolObject type in enumValue)
			pool.Add(type, new Stack<PoolingObject>());
	}

	public static PoolingObject Get(eUIPoolObject key, Transform parTrans = null)
	{
		if (pool[key].TryPop(out var value))
		{
			value.transform.SetParent(parTrans);
			value.Init(key);
			return value;
		}
		else
		{
			value = Instantiate(prefabs[$"{key}"], parTrans);
			value.Init(key);
			return value;
		}
	}

	public static void Release(PoolingObject obj)
	{
		pool[obj.ObjectKey].Push(obj);
		obj.gameObject.SetActive(false);
	}
}
