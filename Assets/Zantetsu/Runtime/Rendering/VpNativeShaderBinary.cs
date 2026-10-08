using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One compiled variant of an indirect VP shader for the Direct3D 12 plugin (DESIGN 4.5.8): per stage, the DXBC
    /// Unity's compiler produced in the Editor and what Unity reported the stage binds -- constant buffers with the
    /// offsets of the constants they use, textures with their samplers, buffers. Written by the Editor's
    /// VpNativeShaderBake into a Resources folder, read here at run time; the plugin's pipeline state is built from
    /// it and from nothing else. The file is the plugin's own: a change of this format changes
    /// <see cref="Version"/> and a file of another version is refused with its name.
    /// <para>
    /// The DXBC Unity returns holds no reflection chunk (checked 2026-10-07: ISGN, OSGN, SHEX only), so the bindings
    /// here are the only way the registers are known.
    /// </para>
    /// </summary>
    public sealed class VpNativeShaderBinary
    {
        public const uint Magic = 0x534E5056; // "VPNS"
        public const int Version = 1;

        /// <summary>The Resources folder the bake writes to and the plugin reads from.</summary>
        public const string ResourceFolder = "VpNative";

        public enum StageKind
        {
            Vertex = 0,
            Pixel = 1,
        }

        public sealed class Constant
        {
            public string Name;
            public int Offset;
            public int Rows;
            public int Columns;
            public int ArraySize;
            public int Bytes => (ArraySize > 0 ? ArraySize : 1) * (Rows > 1 ? Rows * 16 : Columns * 4);
        }

        public sealed class ConstantBuffer
        {
            public string Name;
            public int Register;
            public int Size;
            public readonly List<Constant> Constants = new List<Constant>();

            public Constant Find(string name)
            {
                for (int i = 0; i < Constants.Count; i++)
                {
                    if (string.Equals(Constants[i].Name, name, StringComparison.Ordinal)) return Constants[i];
                }

                return null;
            }
        }

        public sealed class TextureBinding
        {
            public string Name;
            public int Register;
            public int SamplerRegister; // -1: sampled with a shared sampler, or not sampled
            public int Dimension;       // UnityEngine.Rendering.TextureDimension as an int
            public int ArraySize;
        }

        public sealed class BufferBinding
        {
            public string Name;
            public int Register;
        }

        public sealed class SamplerBinding
        {
            public string Name; // empty for a sampler Unity reports without a name (an inline sampler state)
            public int Register;
        }

        public sealed class Stage
        {
            public StageKind Kind;
            public byte[] Bytecode = Array.Empty<byte>();
            public readonly List<ConstantBuffer> ConstantBuffers = new List<ConstantBuffer>();
            public readonly List<TextureBinding> Textures = new List<TextureBinding>();
            public readonly List<BufferBinding> Buffers = new List<BufferBinding>();
            public readonly List<SamplerBinding> Samplers = new List<SamplerBinding>();

            public ConstantBuffer FindConstantBuffer(string name)
            {
                for (int i = 0; i < ConstantBuffers.Count; i++)
                {
                    if (string.Equals(ConstantBuffers[i].Name, name, StringComparison.Ordinal)) return ConstantBuffers[i];
                }

                return null;
            }
        }

        public string Name = "";
        public string Shader = "";
        public string Keywords = "";
        public string UnityVersion = "";
        public readonly List<Stage> Stages = new List<Stage>();

        public Stage FindStage(StageKind kind)
        {
            for (int i = 0; i < Stages.Count; i++)
            {
                if (Stages[i].Kind == kind) return Stages[i];
            }

            return null;
        }

        public byte[] Write()
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Name);
                writer.Write(Shader);
                writer.Write(Keywords);
                writer.Write(UnityVersion);
                writer.Write(Stages.Count);
                foreach (Stage stage in Stages)
                {
                    writer.Write((int)stage.Kind);
                    writer.Write(stage.Bytecode.Length);
                    writer.Write(stage.Bytecode);
                    writer.Write(stage.ConstantBuffers.Count);
                    foreach (ConstantBuffer buffer in stage.ConstantBuffers)
                    {
                        writer.Write(buffer.Name);
                        writer.Write(buffer.Register);
                        writer.Write(buffer.Size);
                        writer.Write(buffer.Constants.Count);
                        foreach (Constant constant in buffer.Constants)
                        {
                            writer.Write(constant.Name);
                            writer.Write(constant.Offset);
                            writer.Write(constant.Rows);
                            writer.Write(constant.Columns);
                            writer.Write(constant.ArraySize);
                        }
                    }

                    writer.Write(stage.Textures.Count);
                    foreach (TextureBinding texture in stage.Textures)
                    {
                        writer.Write(texture.Name);
                        writer.Write(texture.Register);
                        writer.Write(texture.SamplerRegister);
                        writer.Write(texture.Dimension);
                        writer.Write(texture.ArraySize);
                    }

                    writer.Write(stage.Buffers.Count);
                    foreach (BufferBinding buffer in stage.Buffers)
                    {
                        writer.Write(buffer.Name);
                        writer.Write(buffer.Register);
                    }

                    writer.Write(stage.Samplers.Count);
                    foreach (SamplerBinding sampler in stage.Samplers)
                    {
                        writer.Write(sampler.Name ?? "");
                        writer.Write(sampler.Register);
                    }
                }

                writer.Flush();
                return memory.ToArray();
            }
        }

        /// <summary>Reads a file this wrote. False, with the reason, for anything else, including another version.</summary>
        public static bool TryRead(byte[] bytes, out VpNativeShaderBinary binary, out string failure)
        {
            binary = null;
            failure = null;
            if (bytes == null || bytes.Length < 8)
            {
                failure = "no bytes";
                return false;
            }

            try
            {
                using (var memory = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(memory, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != Magic)
                    {
                        failure = "not a VpNativeShaderBinary (magic)";
                        return false;
                    }

                    int version = reader.ReadInt32();
                    if (version != Version)
                    {
                        failure = "VpNativeShaderBinary version " + version + ", this reads " + Version;
                        return false;
                    }

                    var result = new VpNativeShaderBinary
                    {
                        Name = reader.ReadString(),
                        Shader = reader.ReadString(),
                        Keywords = reader.ReadString(),
                        UnityVersion = reader.ReadString(),
                    };
                    int stages = reader.ReadInt32();
                    for (int s = 0; s < stages; s++)
                    {
                        var stage = new Stage { Kind = (StageKind)reader.ReadInt32() };
                        stage.Bytecode = reader.ReadBytes(reader.ReadInt32());
                        int buffers = reader.ReadInt32();
                        for (int b = 0; b < buffers; b++)
                        {
                            var buffer = new ConstantBuffer { Name = reader.ReadString(), Register = reader.ReadInt32(), Size = reader.ReadInt32() };
                            int constants = reader.ReadInt32();
                            for (int c = 0; c < constants; c++)
                            {
                                buffer.Constants.Add(new Constant
                                {
                                    Name = reader.ReadString(), Offset = reader.ReadInt32(), Rows = reader.ReadInt32(), Columns = reader.ReadInt32(), ArraySize = reader.ReadInt32(),
                                });
                            }

                            stage.ConstantBuffers.Add(buffer);
                        }

                        int textures = reader.ReadInt32();
                        for (int t = 0; t < textures; t++)
                        {
                            stage.Textures.Add(new TextureBinding
                            {
                                Name = reader.ReadString(), Register = reader.ReadInt32(), SamplerRegister = reader.ReadInt32(), Dimension = reader.ReadInt32(), ArraySize = reader.ReadInt32(),
                            });
                        }

                        int resources = reader.ReadInt32();
                        for (int r = 0; r < resources; r++)
                        {
                            stage.Buffers.Add(new BufferBinding { Name = reader.ReadString(), Register = reader.ReadInt32() });
                        }

                        int samplers = reader.ReadInt32();
                        for (int r = 0; r < samplers; r++)
                        {
                            stage.Samplers.Add(new SamplerBinding { Name = reader.ReadString(), Register = reader.ReadInt32() });
                        }

                        result.Stages.Add(stage);
                    }

                    binary = result;
                    return true;
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is ObjectDisposedException)
            {
                failure = "VpNativeShaderBinary could not be read: " + e.Message;
                return false;
            }
        }

        /// <summary>One line per binding, for a log or a record.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append(Name).Append(": ").Append(Shader).Append(" [").Append(Keywords).Append("] Unity ").Append(UnityVersion).Append('\n');
            foreach (Stage stage in Stages)
            {
                text.Append("  ").Append(stage.Kind).Append(' ').Append(stage.Bytecode.Length).Append(" bytes\n");
                foreach (ConstantBuffer buffer in stage.ConstantBuffers)
                {
                    text.Append("    cbuffer b").Append(buffer.Register).Append(' ').Append(buffer.Name).Append(' ').Append(buffer.Size).Append(" B:");
                    foreach (Constant constant in buffer.Constants)
                    {
                        text.Append(' ').Append(constant.Name).Append('@').Append(constant.Offset);
                    }

                    text.Append('\n');
                }

                foreach (TextureBinding texture in stage.Textures)
                {
                    text.Append("    texture t").Append(texture.Register).Append(' ').Append(texture.Name).Append(" sampler s").Append(texture.SamplerRegister).Append('\n');
                }

                foreach (BufferBinding buffer in stage.Buffers)
                {
                    text.Append("    buffer t").Append(buffer.Register).Append(' ').Append(buffer.Name).Append('\n');
                }

                foreach (SamplerBinding sampler in stage.Samplers)
                {
                    text.Append("    sampler s").Append(sampler.Register).Append(' ').Append(sampler.Name).Append('\n');
                }
            }

            return text.ToString();
        }
    }
}
