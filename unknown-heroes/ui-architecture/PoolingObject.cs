using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PoolingObject : MonoBehaviour
{
	private Define.eUIPoolObject objectKey;

	public Define.eUIPoolObject ObjectKey => objectKey;

	protected virtual void Awake() { }
	protected virtual void Start() { }

	public void Init(Define.eUIPoolObject key)
	{
		objectKey = key;
		gameObject.SetActive(true);
		Init();
	}

	protected virtual void Init()
	{
		//pass
	}

	public virtual void Play()
	{
		//pass
	}

	public virtual void Stop()
	{
		Release();
	}

	private void Release() => UI_Pool.Release(this);
}
