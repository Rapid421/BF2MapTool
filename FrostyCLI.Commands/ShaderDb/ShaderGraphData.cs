using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderDataPlugin
{
    [EbxClassMeta(EbxFieldType.Struct)]
    public class PermutationPair
    {
        [IsReadOnly]
        [DisplayName("Pixel Shader")]
        [Category("Main")]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public ShaderGraphPermutation PixelShader => ps;

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public ShaderGraphPermutation VertexShader => vs;

        public ShaderGraphPermutation ps = new ShaderGraphPermutation();
        public ShaderGraphPermutation vs = new ShaderGraphPermutation();
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class RenderPath
    {
        [IsReadOnly]
        [Category("Main")]
        public CString RenderPathName { get; set; } = "";

        [IsReadOnly]
        [DisplayName("Pixel Shader Permutations")]
        [Category("Main")]
        [Description("Only non-empty pixel-shader permutations are shown.")]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<PermutationPair> PermutationPairs { get; set; } = new List<PermutationPair>();
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class ShaderGraphData
    {
        [IsReadOnly]
        [Category("Annotations")]
        [EbxFieldMeta(EbxFieldType.Guid)]
        public Guid Guid { get; set; } = default;

        [IsReadOnly]
        [Category("Annotations")]
        public CString Name { get; set; } = "";

        [IsReadOnly]
        [DisplayName("Main Shader Data")]
        [Category("Main")]
        [Description("Pixel-shader texture inputs and value parameters for this selected shader.")]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<RenderPath> RenderPaths { get; set; } = new List<RenderPath>();

    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class ExternalValue
    {
        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Name { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.Struct)]
        public dynamic DefaultValue { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Type { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool Required { get; set; }

        public ExternalValue()
        {
            DefaultValue = TypeLibrary.CreateObject("Vec4");
            DefaultValue.x = 1f;
            DefaultValue.y = 1f;
            DefaultValue.z = 1f;
            DefaultValue.w = 1f;
            //Type = TypeLibrary.CreateObject("ExternalValueConstantType");
        }
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class ExternalBuffer
    {
        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Name { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Type { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool Required { get; set; }

        public ExternalBuffer()
        {
            //Type = TypeLibrary.CreateObject("ShaderValueType");
        }
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class ConstantFunction
    {
        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Name
        {
            get
            {
                Type type = TypeLibrary.GetType("ShaderConstantFunction");
                string funcStr;
                if (type != null)
                {
                    funcStr = type.GetEnumName(funcType);
                    if (!string.IsNullOrEmpty(funcStr))
                    {
                        funcStr = funcStr.Replace("ShaderConstantFunction_", "");
                        funcStr = char.ToLowerInvariant(funcStr[0]) + funcStr.Substring(1);
                    }
                    else
                    {
                        funcStr = "INVALID";
                    }
                }
                else
                {
                    funcStr = "INVALID";
                }
                return funcStr;
            }
            //set;
        }

        //[IsReadOnly]
        //[EbxFieldMeta(EbxFieldType.CString)]
        //public CString Type { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.UInt32)]
        public uint CBufferIndex { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.UInt32)]
        public uint ArraySize { get; set; }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.UInt32)]
        public uint MatrixDims { get; set; }

        public uint funcType;
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class TextureFunction
    {
        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Name
        {
            get
            {
                Type type = TypeLibrary.GetType("ShaderConstantFunction");
                string funcStr;
                if (type != null)
                {
                    funcStr = type.GetEnumName(funcType);
                    if (!string.IsNullOrEmpty(funcStr))
                    {
                        funcStr = funcStr.Replace("ShaderConstantFunction_", "");
                        funcStr = char.ToLowerInvariant(funcStr[0]) + funcStr.Substring(1);
                    }
                    else
                    {
                        funcStr = "INVALID";
                    }
                }
                else
                {
                    funcStr = "INVALID";
                }
                return funcStr;
            }
            //set;
        }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Type
        {
            get
            {
                Type type = TypeLibrary.GetType("ShaderValueType");
                string funcStr;
                if (type != null)
                {
                    funcStr = type.GetEnumName(texType);
                    if (string.IsNullOrEmpty(funcStr))
                    {
                        funcStr = "INVALID";
                    }
                }
                else
                {
                    funcStr = "INVALID";
                }
                return funcStr;
            }
            //set;
        }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.UInt32)]
        public uint Index { get; set; }

        public uint funcType;
        public uint texType;
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class TextureBinding
    {
        [IsReadOnly]
        [IsHidden]
        [DisplayName("Slot")]
        public uint Slot { get; set; }

        [IsReadOnly]
        [IsHidden]
        [DisplayName("Texture")]
        [Description("Resolved texture asset name, or the original shader texture reference.")]
        public CString Texture { get; set; } = "";

        [IsReadOnly]
        [IsHidden]
        [DisplayName("Reference Type")]
        public CString ReferenceType { get; set; } = "";

        [IsReadOnly]
        [IsHidden]
        [DisplayName("Shader Function")]
        public CString ShaderFunction { get; set; } = "";

        [IsReadOnly]
        [IsHidden]
        [DisplayName("Texture Type")]
        public CString TextureType { get; set; } = "";

        [IsReadOnly]
        [IsHidden]
        [DisplayName("Hash")]
        public CString Hash { get; set; } = "";
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class BufferFunction
    {
        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Name
        {
            get
            {
                Type type = TypeLibrary.GetType("ShaderConstantFunction");
                string funcStr;
                if (type != null)
                {
                    funcStr = type.GetEnumName(funcType);
                    if (!string.IsNullOrEmpty(funcStr))
                    {
                        funcStr = funcStr.Replace("ShaderConstantFunction_", "");
                        funcStr = char.ToLowerInvariant(funcStr[0]) + funcStr.Substring(1);
                    }
                    else
                    {
                        funcStr = "INVALID";
                    }
                }
                else
                {
                    funcStr = "INVALID";
                }
                return funcStr;
            }
            //set;
        }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.CString)]
        public CString Type
        {
            get
            {
                Type type = TypeLibrary.GetType("ShaderValueType");
                string funcStr;
                if (type != null)
                {
                    funcStr = type.GetEnumName(bufType);
                    if (string.IsNullOrEmpty(funcStr))
                    {
                        funcStr = "INVALID";
                    }
                }
                else
                {
                    funcStr = "INVALID";
                }
                return funcStr;
            }
            //set;
        }

        [IsReadOnly]
        [EbxFieldMeta(EbxFieldType.UInt32)]
        public uint Index { get; set; }

        public uint funcType;
        public uint bufType;
    }

    [EbxClassMeta(EbxFieldType.Struct)]
    public class ShaderGraphPermutation
    {
        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<ConstantFunction> ConstantFunctions { get; set; } = new List<ConstantFunction>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<TextureFunction> TextureFunctions { get; set; } = new List<TextureFunction>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<BufferFunction> BufferFunctions { get; set; } = new List<BufferFunction>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Inherited)]
        public List<dynamic> ValueConstants { get; set; } = new List<dynamic>();

        [IsReadOnly]
        [DisplayName("Constant Textures")]
        [Category("Main")]
        [Description("Texture asset names referenced directly by this pixel shader.")]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.CString)]
        public List<CString> TextureConstants { get; set; } = new List<CString>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<TextureBinding> TextureBindings { get; set; } = new List<TextureBinding>();

        [IsReadOnly]
        [DisplayName("Value Parameters")]
        [Category("Main")]
        [Description("Contains default values, names, and other information for values that are passed into a shader externally.")]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<ExternalValue> ExternalValueConstants { get; set; } = new List<ExternalValue>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.CString)]
        public List<CString> ExternalTextureConstants { get; set; } = new List<CString>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<ExternalBuffer> ExternalBufferConstants { get; set; } = new List<ExternalBuffer>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<SamplerState> SamplerStates { get; set; } = new List<SamplerState>();

        [IsReadOnly]
        [IsHidden]
        [EbxFieldMeta(EbxFieldType.Array, arrayType: EbxFieldType.Struct)]
        public List<VertexElementBase> VertexElements { get; set; } = new List<VertexElementBase>();

        // won't be shown in the editor, contains data to look up the bytecode for this shader
        public ShaderPermutation shaderDataLookup = new ShaderPermutation();
    }
}
