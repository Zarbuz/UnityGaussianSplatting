// SPDX-License-Identifier: MIT

using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatAsset : ScriptableObject
    {
        public const int kCurrentVersion = 2023_10_20;
        public const int kChunkSize = 256;
        public const int kTextureWidth = 2048; // allows up to 32M splats on desktop GPU (2k width x 16k height)
        public const int kMaxSplats = 8_600_000; // mostly due to 2GB GPU buffer size limit when exporting a splat (2GB / 248B is just over 8.6M)

        [SerializeField] int m_FormatVersion;
        [SerializeField] int m_SplatCount;
        [SerializeField] Vector3 m_BoundsMin;
        [SerializeField] Vector3 m_BoundsMax;
        [SerializeField] Hash128 m_DataHash;

        public int formatVersion => m_FormatVersion;
        public int splatCount => m_SplatCount;
        public Vector3 boundsMin => m_BoundsMin;
        public Vector3 boundsMax => m_BoundsMax;
        public Hash128 dataHash => m_DataHash;

        // Match VECTOR_FMT_* in HLSL
        public enum VectorFormat
        {
            Float32, // 12 bytes: 32F.32F.32F
            Norm16, // 6 bytes: 16.16.16
            Norm11, // 4 bytes: 11.10.11
            Norm6   // 2 bytes: 6.5.5
        }

        public static int GetVectorSize(VectorFormat fmt)
        {
            return fmt switch
            {
                VectorFormat.Float32 => 12,
                VectorFormat.Norm16 => 6,
                VectorFormat.Norm11 => 4,
                VectorFormat.Norm6 => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public enum ColorFormat
        {
            Float32x4,
            Float16x4,
            Norm8x4,
            BC7,
        }
        public static int GetColorSize(ColorFormat fmt)
        {
            return fmt switch
            {
                ColorFormat.Float32x4 => 16,
                ColorFormat.Float16x4 => 8,
                ColorFormat.Norm8x4 => 4,
                ColorFormat.BC7 => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public enum SHFormat
        {
            Float32,
            Float16,
            Norm11,
            Norm6,
            Cluster64k,
            Cluster32k,
            Cluster16k,
            Cluster8k,
            Cluster4k,
        }

        public struct SHTableItemFloat32
        {
            public float3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public float3 shPadding; // pad to multiple of 16 bytes
        }
        public struct SHTableItemFloat16
        {
            public half3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public half3 shPadding; // pad to multiple of 16 bytes
        }
        public struct SHTableItemNorm11
        {
            public uint sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        }
        public struct SHTableItemNorm6
        {
            public ushort sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public ushort shPadding; // pad to multiple of 4 bytes
        }

        public void Initialize(int splats, VectorFormat formatPos, VectorFormat formatScale, ColorFormat formatColor, SHFormat formatSh, Vector3 bMin, Vector3 bMax, CameraInfo[] cameraInfos)
        {
            m_SplatCount = splats;
            m_FormatVersion = kCurrentVersion;
            m_PosFormat = formatPos;
            m_ScaleFormat = formatScale;
            m_ColorFormat = formatColor;
            m_SHFormat = formatSh;
            m_Cameras = cameraInfos;
            m_BoundsMin = bMin;
            m_BoundsMax = bMax;
        }

        public void SetDataHash(Hash128 hash)
        {
            m_DataHash = hash;
        }

        public void SetAssetFiles(TextAsset dataChunk, TextAsset dataPos, TextAsset dataOther, TextAsset dataColor, TextAsset dataSh)
        {
            m_ChunkData = dataChunk;
            m_PosData = dataPos;
            m_OtherData = dataOther;
            m_ColorData = dataColor;
            m_SHData = dataSh;
        }

        /// <summary>
        /// Initialize default LOD levels based on LODGE paper recommendations
        /// Creates 4 LOD levels with exponentially increasing distance thresholds
        /// </summary>
        public void InitializeDefaultLODLevels()
        {
            // Default LOD configuration: 4 levels with exponential distance distribution
            // Based on LODGE paper: d_0=5m, d_1=10m, d_2=20m, d_3=40m, d_4=∞
            m_LODLevels = new LODLevel[4];

            // LOD 0: High detail (0-10m)
            m_LODLevels[0] = new LODLevel
            {
                distanceThreshold = 10f,
                startSplatIndex = 0,
                splatCount = m_SplatCount,  // Full resolution
                smoothingFactor = 0f        // No smoothing for closest LOD
            };

            // LOD 1: Medium-high detail (10-20m)
            m_LODLevels[1] = new LODLevel
            {
                distanceThreshold = 20f,
                startSplatIndex = 0,
                splatCount = m_SplatCount,  // Will be pruned during preprocessing
                smoothingFactor = 0.5f
            };

            // LOD 2: Medium detail (20-40m)
            m_LODLevels[2] = new LODLevel
            {
                distanceThreshold = 40f,
                startSplatIndex = 0,
                splatCount = m_SplatCount,
                smoothingFactor = 1.0f
            };

            // LOD 3: Low detail (40m+)
            m_LODLevels[3] = new LODLevel
            {
                distanceThreshold = float.MaxValue,
                startSplatIndex = 0,
                splatCount = m_SplatCount,
                smoothingFactor = 2.0f
            };

            m_UseLOD = true;
        }

        /// <summary>
        /// Set custom LOD levels
        /// </summary>
        public void SetLODLevels(LODLevel[] levels, bool enable = true)
        {
            m_LODLevels = levels;
            m_UseLOD = enable;
        }

        /// <summary>
        /// Set LOD data files for each level
        /// </summary>
        public void SetLODDataFiles(LODDataFiles[] dataFiles)
        {
            m_LODDataFiles = dataFiles;
        }

        /// <summary>
        /// Get data files for a specific LOD level (falls back to base data if not available)
        /// </summary>
        public void GetDataForLODLevel(int lodLevel, out TextAsset pos, out TextAsset other, out TextAsset color, out TextAsset sh, out TextAsset chunk, out int splatCount)
        {
            // If we have LOD-specific data files, use them
            if (hasLODDataFiles && lodLevel >= 0 && lodLevel < m_LODDataFiles.Length)
            {
                var lodData = m_LODDataFiles[lodLevel];
                if (lodData.posData != null) // Check if this LOD level has data
                {
                    pos = lodData.posData;
                    other = lodData.otherData;
                    color = lodData.colorData;
                    sh = lodData.shData;
                    chunk = lodData.chunkData;
                    splatCount = lodData.splatCount;
                    return;
                }
            }

            // Fall back to base data
            pos = m_PosData;
            other = m_OtherData;
            color = m_ColorData;
            sh = m_SHData;
            chunk = m_ChunkData;
            splatCount = m_SplatCount;
        }

        public static int GetOtherSizeNoSHIndex(VectorFormat scaleFormat)
        {
            return 4 + GetVectorSize(scaleFormat);
        }

        public static int GetSHCount(SHFormat fmt, int splatCount)
        {
            return fmt switch
            {
                SHFormat.Float32 => splatCount,
                SHFormat.Float16 => splatCount,
                SHFormat.Norm11 => splatCount,
                SHFormat.Norm6 => splatCount,
                SHFormat.Cluster64k => 64 * 1024,
                SHFormat.Cluster32k => 32 * 1024,
                SHFormat.Cluster16k => 16 * 1024,
                SHFormat.Cluster8k => 8 * 1024,
                SHFormat.Cluster4k => 4 * 1024,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public static (int,int) CalcTextureSize(int splatCount)
        {
            int width = kTextureWidth;
            int height = math.max(1, (splatCount + width - 1) / width);
            // our swizzle tiles are 16x16, so make texture multiple of that height
            int blockHeight = 16;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;
            return (width, height);
        }

        public static GraphicsFormat ColorFormatToGraphics(ColorFormat format)
        {
            return format switch
            {
                ColorFormat.Float32x4 => GraphicsFormat.R32G32B32A32_SFloat,
                ColorFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
                ColorFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
                ColorFormat.BC7 => GraphicsFormat.RGBA_BC7_UNorm,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
        }

        public static long CalcPosDataSize(int splatCount, VectorFormat formatPos)
        {
            return splatCount * GetVectorSize(formatPos);
        }
        public static long CalcOtherDataSize(int splatCount, VectorFormat formatScale)
        {
            return splatCount * GetOtherSizeNoSHIndex(formatScale);
        }
        public static long CalcColorDataSize(int splatCount, ColorFormat formatColor)
        {
            var (width, height) = CalcTextureSize(splatCount);
            return width * height * GetColorSize(formatColor);
        }
        public static long CalcSHDataSize(int splatCount, SHFormat formatSh)
        {
            int shCount = GetSHCount(formatSh, splatCount);
            return formatSh switch
            {
                SHFormat.Float32 => shCount * UnsafeUtility.SizeOf<SHTableItemFloat32>(),
                SHFormat.Float16 => shCount * UnsafeUtility.SizeOf<SHTableItemFloat16>(),
                SHFormat.Norm11 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm11>(),
                SHFormat.Norm6 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm6>(),
                _ => shCount * UnsafeUtility.SizeOf<SHTableItemFloat16>() + splatCount * 2
            };
        }
        public static long CalcChunkDataSize(int splatCount)
        {
            int chunkCount = (splatCount + kChunkSize - 1) / kChunkSize;
            return chunkCount * UnsafeUtility.SizeOf<ChunkInfo>();
        }

        [SerializeField] VectorFormat m_PosFormat = VectorFormat.Norm11;
        [SerializeField] VectorFormat m_ScaleFormat = VectorFormat.Norm11;
        [SerializeField] SHFormat m_SHFormat = SHFormat.Norm11;
        [SerializeField] ColorFormat m_ColorFormat;

        [SerializeField] TextAsset m_PosData;
        [SerializeField] TextAsset m_ColorData;
        [SerializeField] TextAsset m_OtherData;
        [SerializeField] TextAsset m_SHData;
        // Chunk data is optional (if data formats are fully lossless then there's no chunking)
        [SerializeField] TextAsset m_ChunkData;

        [SerializeField] CameraInfo[] m_Cameras;

        // LOD system data
        [SerializeField] LODLevel[] m_LODLevels;
        [SerializeField] bool m_UseLOD = false;

        // LOD data files (one set per LOD level)
        // Each LOD level can have its own pruned/filtered splat data
        [SerializeField] LODDataFiles[] m_LODDataFiles;

        public VectorFormat posFormat => m_PosFormat;
        public VectorFormat scaleFormat => m_ScaleFormat;
        public SHFormat shFormat => m_SHFormat;
        public ColorFormat colorFormat => m_ColorFormat;

        public TextAsset posData => m_PosData;
        public TextAsset colorData => m_ColorData;
        public TextAsset otherData => m_OtherData;
        public TextAsset shData => m_SHData;
        public TextAsset chunkData => m_ChunkData;
        public CameraInfo[] cameras => m_Cameras;

        // LOD accessors
        public LODLevel[] lodLevels => m_LODLevels;
        public bool useLOD => m_UseLOD;
        public int lodLevelCount => m_LODLevels?.Length ?? 0;
        public LODDataFiles[] lodDataFiles => m_LODDataFiles;
        public bool hasLODDataFiles => m_LODDataFiles != null && m_LODDataFiles.Length > 0;

        public struct ChunkInfo
        {
            public uint colR, colG, colB, colA;
            public float2 posX, posY, posZ;
            public uint sclX, sclY, sclZ;
            public uint shR, shG, shB;
        }

        // LOD (Level of Detail) system structures based on LODGE paper
        [Serializable]
        public struct LODLevel
        {
            public float distanceThreshold;     // Distance threshold for this LOD level (d_l in paper)
            public int startSplatIndex;         // Start index of splats for this LOD level
            public int splatCount;              // Number of splats in this LOD level
            public float smoothingFactor;       // 3D smoothing filter factor (s_d/f in paper)
        }

        // Data files for a specific LOD level
        [Serializable]
        public struct LODDataFiles
        {
            public TextAsset posData;
            public TextAsset otherData;
            public TextAsset colorData;
            public TextAsset shData;
            public TextAsset chunkData;
            public int splatCount;
        }

        [Serializable]
        public struct CameraInfo
        {
            public Vector3 pos;
            public Vector3 axisX, axisY, axisZ;
            public float fov;
        }
    }
}
