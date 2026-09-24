using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    public static class Compact16uvAtlasBuild
    {
        private const string Root="Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas";
        public static void Run()
        {
            string source="Assets/Licensed/Compact16uvIntake/character-casual/textures/Textures1_bottom_256.png";
            using(var sha=SHA256.Create())
                if(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(source))).Replace("-","").ToLowerInvariant()!="88fdb7640dcadbef46db9bb52a533180ae0ef2d5bec5bea599d024c3614ebcc5")throw new Exception("Source palette hash changed");
            var texture=new Texture2D(2,2,TextureFormat.RGBA32,false);
            try
            {
                if(!texture.LoadImage(File.ReadAllBytes(source))||texture.width!=256||texture.height!=256)throw new Exception("256x256 source required");
                Color32[] original=texture.GetPixels32();
                Directory.CreateDirectory(Root);
                foreach(bool debug in new[]{false,true})
                {
                    var pixels=(Color32[])original.Clone();
                    Paint(pixels,247,debug ? new Color32(38,191,51,255) : new Color32(82,84,89,255));
                    Paint(pixels,239,debug ? new Color32(255,0,0,255) : new Color32(82,84,89,255));
                    for(int i=0;i<256*128;i++)if(!pixels[i].Equals(original[i]))throw new Exception("Source surface changed");
                    texture.SetPixels32(pixels);texture.Apply(false);
                    string path=Root+(debug ? "/Debug.png" : "/Normal.png");
                    File.WriteAllBytes(path,texture.EncodeToPNG());AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                    var importer=(TextureImporter)AssetImporter.GetAtPath(path);
                    importer.sRGBTexture=true;importer.mipmapEnabled=true;importer.isReadable=false;importer.filterMode=FilterMode.Bilinear;
                    importer.wrapMode=TextureWrapMode.Repeat;importer.anisoLevel=1;importer.npotScale=TextureImporterNPOTScale.None;
                    importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name="Standalone",overridden=true,format=TextureImporterFormat.DXT5,maxTextureSize=256,compressionQuality=100 });
                    importer.SaveAndReimport();
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(texture);}
            string[] names={"VP Unlit","VP Indirect Unlit","VP Indexed Indirect Unlit","VP Culled Indexed Indirect Unlit","VP Indirect Shadow Caster","VP Indexed Indirect Shadow Caster","VP Stencil Init","VP Stencil Volume","VP Stencil Cap"};
            for(int i=0;i<names.Length;i++)
            {
                string path=$"Assets/Zantetsu/Tests/StandaloneRendering/Resources/VpAtlasProbe{i}.mat";
                var shader=Shader.Find("Zantetsu/"+names[i]);if(shader==null||!shader.isSupported)throw new Exception("Shader missing/unsupported: "+names[i]);
                var material=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(material==null){material=new Material(shader);AssetDatabase.CreateAsset(material,path);}else material.shader=shader;
            }
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("docs/diagnostics/compact16uv-atlas");
            var pair=new[]{"Normal","Debug"}.Select(name=>{
                var t=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/"+name+".png");
                return new Info{name=name,format=t.format.ToString(),mips=t.mipmapCount,width=t.width,height=t.height,filter=t.filterMode.ToString(),wrap=t.wrapMode.ToString(),aniso=t.anisoLevel,residentBytes=UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t)};
            }).ToArray();
            File.WriteAllText("docs/diagnostics/compact16uv-atlas/build.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,textures=pair},true));
            Debug.Log("Prepared atlas pair: unchanged source bottom half, real247/provisional239 x y247, 5x5 patches; DXT5 sRGB 9 mips.");
        }
        private static void Paint(Color32[] pixels,int x,Color32 colour){for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)pixels[(247+dy)*256+x+dx]=colour;}
        [Serializable] private sealed class Report{public string unity;public Info[] textures;}
        [Serializable] private sealed class Info{public string name,format,filter,wrap;public int width,height,mips,aniso;public long residentBytes;}
    }
}
