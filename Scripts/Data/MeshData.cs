﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using IntVector3 = Mono.Simd.Vector4i;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine.VR.Utilities;

[Serializable]
public class MeshData : ISerializable
{
	const int k_DataVersion = 0;
    public Vector3[] vertices { get; private set; }
    public float cellSize { get; private set; }
    public readonly Dictionary<IntVector3, List<IntVector3>> triBuckets = new Dictionary<IntVector3, List<IntVector3>>();
    public bool processed { get; private set; }

#if UNITY_EDITOR
    public bool expanded, bucketsExpanded;          //State variables for foldouts
#endif
    public string name;                             //For debug

    const int k_maxTrisPerFrame = 1000000;
    const float k_minCellSize = 0.05f;

    static readonly Dictionary<Mesh, MeshData> meshDatas = new Dictionary<Mesh, MeshData>();
    int[] triangles;        //This gets freed after setup
    IEnumerator resume;
    int triProcessed;
    long totalTriProcessed;
    long triTotal = 1;

    MeshData(Mesh mesh) {
        vertices = mesh.vertices;
        triangles = mesh.triangles;
    }

    protected MeshData(SerializationInfo info, StreamingContext context)
    {
	    int dataVersion = info.GetInt32("dataVersion");

		if (dataVersion != k_DataVersion)
	    {
		    Debug.LogWarning("Mesh data serialized with wrong version: " + dataVersion);
	    }
        cellSize = info.GetSingle("cellSize");
        List<SerialKVP> tmpBuckets = (List<SerialKVP>)info.GetValue("triBuckets", typeof(List<SerialKVP>));
        foreach (var tmpBucket in tmpBuckets) {
            List<IntVector3> tris = new List<IntVector3>(tmpBucket.Value.Count);
            triBuckets[tmpBucket.Key] = tris;
            tris.AddRange(tmpBucket.Value.Select(tri => (IntVector3) tri));
        }                                                 
    }

	static MeshData()
	{
		ProcessProjectMeshes();
	}
    //TODO: kill threads on reload
    public static readonly Dictionary<string, MeshDataProgress> progress = new Dictionary<string, MeshDataProgress>();
    static string cachePath = "../Library/Meshes";

    static string _fullCachePath;                //Cache the Value for thread access
    public static string fullCachePath {
        get {
            if (string.IsNullOrEmpty(_fullCachePath))
                _fullCachePath = Path.Combine(Application.dataPath, cachePath);
            return _fullCachePath;
        }
    }
#if UNITY_EDITOR
    public static Dictionary<Mesh, MeshData> GetMeshDataDictionary()
    {
	    return meshDatas;
    } 
    public static string SerializedMeshPath(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        if (!string.IsNullOrEmpty(path))
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            return SerializedMeshPath(guid, mesh.name);
        } else
        {
            //TODO: handle non-asset meshes
        }
        return null;
    }
#else
    //TODO: player support
#endif
    public static string SerializedMeshPath(string guid, string name) {
        return Path.Combine(fullCachePath, guid + "-" + name + ".meshdata");
    } 

    public static bool ValidMesh(Mesh mesh)
    {                                                    
        return !string.IsNullOrEmpty(SerializedMeshPath(mesh));
    }

    //NOTE: Must be called from the main thread
    public static MeshData GetMeshData(Mesh mesh)
    {                   
        MeshData result;
        if (meshDatas.TryGetValue(mesh, out result))
        {                         
            return result;
        }
        string path = SerializedMeshPath(mesh);
        if (File.Exists(path))
        {                     
            using (var file = File.Open(path,FileMode.Open))
            {
                result = (MeshData)new BinaryFormatter().Deserialize(file);
                result.name = mesh.name;
                result.vertices = mesh.vertices;
                result.processed = true;
            }
        } else
        {
            result = ProcessMesh(mesh);
        }
        meshDatas[mesh] = result;
        return result;
    }

    public IEnumerable Setup() {
        if (processed)
            yield break;
        if (resume == null) {
            //NB: picking an object cell size is tough. Right now we assume unifrom distribution of vertices, which is obviously not consistent
            //cellSize = (Mathf.Min(sceneObject.bounds.size.x, sceneObject.bounds.size.y, sceneObject.bounds.size.z) / (vertices.Length / 3)) * k_cellSizeFactor;
            Vector3 size = Vector3.zero;
            foreach (var vector3 in vertices) {
                Vector3 abs = new Vector3(
                    Mathf.Abs(vector3.x),
                    Mathf.Abs(vector3.y),
                    Mathf.Abs(vector3.z));
                if (abs.x > size.x)
                    size.x = abs.x;
                if (abs.y > size.y)
                    size.y = abs.y;
                if (abs.z > size.z)
                    size.z = abs.z;
            }
            size *= 2;
            float maxSideLength = Mathf.Max(size.x, size.y, size.z);
            cellSize = Mathf.Pow(maxSideLength / vertices.Length, 0.333333f);
            if (cellSize < k_minCellSize * maxSideLength)
                cellSize = k_minCellSize * maxSideLength;

            resume = SpatializeLocal(cellSize, size * 0.5f, triangles);
            yield return null;
        }
        while (resume.MoveNext()) {
            yield return null;
        }                                  
        processed = true;
        triangles = null;
    }

    IEnumerator SpatializeLocal(float cellSize, Vector3 extents, int[] triangles) {
        IntVector3 lowerLeft = SpatialHasher.SnapToGrid(-extents - Vector3.one * cellSize * 0.5f, cellSize);
        IntVector3 upperRight = SpatialHasher.SnapToGrid(extents + Vector3.one * cellSize * 0.5f, cellSize);
        IntVector3 diff = upperRight - lowerLeft + IntVector3.Identity;
        triTotal = diff.X * diff.Y * diff.Z * (long)triangles.Length / 3;
        for (int x = lowerLeft.X; x <= upperRight.X; x++) {
            for (int y = lowerLeft.Y; y <= upperRight.Y; y++) {
                for (int z = lowerLeft.Z; z <= upperRight.Z; z++) {
                    for (int i = 0; i < triangles.Length; i += 3) {
                        int triA = triangles[i];
                        int triB = triangles[i + 1];
                        int triC = triangles[i + 2];
                        Vector3 a = vertices[triA];
                        Vector3 b = vertices[triB];
                        Vector3 c = vertices[triC];
                                                
                        triProcessed++;
                        totalTriProcessed++;
                        if (triProcessed++ > k_maxTrisPerFrame)
                        {
                            triProcessed = 0;
                            yield return null;
                        }
                        IntVector3 currMin = new IntVector3(x, y, z, 0);
                        Bounds box = new Bounds { min = currMin.mul(cellSize) };
                        box.max = box.min + Vector3.one * cellSize;          
                        if (U.Intersection.TestTriangleAABB(a, b, c, box)) {
                            List<IntVector3> tris;
                            if (!triBuckets.TryGetValue(currMin, out tris)) {
                                tris = new List<IntVector3>();
                                triBuckets[currMin] = tris;
                            }
                            tris.Add(new IntVector3(triA, triB, triC, 0));
                        }
                    }
                }
            }
        }               
    }

	public static void ProcessProjectMeshes()
	{
		MeshFilter[] meshFilters = (MeshFilter[]) Resources.FindObjectsOfTypeAll(typeof (MeshFilter));
		HashSet<Mesh> meshes = new HashSet<Mesh>();
		foreach (var meshFilter in meshFilters)
		{
			if (meshFilter == null || meshFilter.sharedMesh == null)
				continue;
			meshes.Add(meshFilter.sharedMesh);
		}
		foreach (var mesh in meshes)
		{
			if (AssetDatabase.Contains(mesh))
			{	
				string path = AssetDatabase.GetAssetPath(mesh);
				if (!string.IsNullOrEmpty(path) && !path.Contains("unity editor resources"))
				{
					if (!File.Exists(SerializedMeshPath(mesh)))
						ProcessMesh(mesh);
				}
			}
		}
	}

	public static MeshData ProcessMesh(Mesh mesh) {
        string fullPath = SerializedMeshPath(mesh);
        MeshData meshData = new MeshData(mesh);
        MeshDataProgress prog = new MeshDataProgress();
        lock (progress) {
            progress[fullPath] = prog;
        }
        prog.thread = new Thread(() => {
            try {
				//Pause the thread if there is already one running thread per processor
                while (true) {
                    lock (progress) {
                        if (progress.Values.Count(value => value.running) < Environment.ProcessorCount)
                            break;
                    }
                    Thread.Sleep(10);
                }
				//If this mesh doesn't have a progress object, break the loop, otherwise, start set running = true and start the process
                lock (progress) {
                    if (!progress.ContainsKey(fullPath))
                        return;
                    progress[fullPath].running = true;
                }
				//Process mesh, putting triangles into buckets
                foreach (var e in meshData.Setup()) {                                               
                    lock (progress) {
                        if (!progress.ContainsKey(fullPath))	//If the progress object was removed (canceled) kill the thread
                            return;
                        progress[fullPath].progress = (float)meshData.totalTriProcessed / meshData.triTotal;
                    }                         
                    Thread.Sleep(10);         
                }                                                     
				//Serialize the result
                FileStream stream = File.Create(fullPath);
                var formatter = new BinaryFormatter();
                formatter.Serialize(stream, meshData);
                stream.Close();
				//Remove the progress object from the list when we're done
                lock (progress) {
                    progress.Remove(fullPath);
                }
            } catch (Exception e) {
                Debug.Log("error in process " + e.Message);
                lock (progress) {
                    progress.Remove(fullPath);
                }
            }
        });
        prog.thread.Start();
        return meshData;
    }
	  
    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        List<SerialKVP> tmpBuckets = new List<SerialKVP>();
        foreach (var triBucket in triBuckets) {
            var newBucket = new List<SerialIV3>(triBucket.Value.Count);
            var kvp = new SerialKVP {Key = triBucket.Key, Value = newBucket};
            tmpBuckets.Add(kvp);
            newBucket.AddRange(triBucket.Value.Select(tri => (SerialIV3) tri));
        }                         
        info.AddValue("triBuckets", tmpBuckets);
        info.AddValue("cellSize", cellSize);                       
		info.AddValue("dataVersion", k_DataVersion);
    }

    [Serializable]
    class SerialIV3 {   
        public int x, y, z;

        public static implicit operator SerialIV3(IntVector3 vec) {
            return new SerialIV3 { x = vec.X, y = vec.Y, z = vec.Z };
        }
        public static implicit operator IntVector3(SerialIV3 vec) {
            return new IntVector3(vec.x, vec.y, vec.z, 0);
        }
    }
    [Serializable]
    class SerialKVP
    {
        public SerialIV3 Key;
        public List<SerialIV3> Value;
    }

    public static void ClearCache()
    {
        meshDatas.Clear();
    }
}

public class MeshDataProgress {
    public float progress;
    public Thread thread;
    public bool running;
}