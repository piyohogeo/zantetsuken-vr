using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    /// <summary>Private reference inputs for a rendered-image test; never a production Mesh cache.</summary>
    public static class Compact16uvAppearanceBuild
    {
        public static void Run()
        {
            const string root=Compact16uvProductIntake.PrivateRoot;
            var input=JsonUtility.FromJson<Input>(File.ReadAllText(root+"/intake.json"));
            var entry=input.assets.Single(x=>x.family=="static-megacity");
            using(var sha=SHA256.Create())
                if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(entry.assetPath))).Replace("-","").ToLowerInvariant()!=entry.sourceSha256)
                    throw new Exception("Source asset hash changed; redo ordered intake audit first.");
            var model=AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            var mesh=model.GetComponentsInChildren<MeshFilter>(true).Single(x=>x.sharedMesh!=null&&x.sharedMesh.name==entry.objectName).sharedMesh;
            string output=root+"/Resources/AppearanceMigration";
            Directory.CreateDirectory(output);
            string meshPath=output+"/MegacitySource.asset";
            var existing=AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if(existing==null)AssetDatabase.CreateAsset(UnityEngine.Object.Instantiate(mesh),meshPath);
            else EditorUtility.CopySerialized(mesh,existing);
            var shader=Shader.Find("Hidden/Zantetsu/Compact16uv Mesh Oracle");
            if(shader==null||!shader.isSupported)throw new Exception("Mesh oracle shader unsupported");
            string materialPath=output+"/SourcePalette.mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null){material=new Material(shader);AssetDatabase.CreateAsset(material,materialPath);}
            material.shader=shader;
            material.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(root+"/character-casual/textures/Textures1_bottom_256.png"));
            if(material.GetTexture("_BaseMap")==null)throw new Exception("Source palette absent");
            EditorUtility.SetDirty(material);AssetDatabase.SaveAssets();
            Debug.Log($"Appearance reference: {entry.objectName}, {mesh.vertexCount} original float vertices, {mesh.triangles.Length} indices; private Mesh and source palette only.");
        }
        [Serializable] private sealed class Input { public Entry[] assets; }
        [Serializable] private sealed class Entry { public string family,assetPath,sourceSha256,objectName; }
    }
}
