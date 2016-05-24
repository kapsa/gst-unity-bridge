﻿/*
*  GStreamer - Unity3D bridge (GUB).
*  Copyright (C) 2016  Fundacio i2CAT, Internet i Innovacio digital a Catalunya
*
*  This program is free software: you can redistribute it and/or modify
*  it under the terms of the GNU Lesser General Public License as published by
*  the Free Software Foundation, either version 3 of the License, or
*  (at your option) any later version.
*
*  This program is distributed in the hope that it will be useful,
*  but WITHOUT ANY WARRANTY; without even the implied warranty of
*  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
*  GNU Lesser General Public License for more details.
*
*  You should have received a copy of the GNU Lesser General Public License
*  along with this program.  If not, see <http://www.gnu.org/licenses/>.
*
*  Authors:  Xavi Artigas <xavi.artigas@i2cat.net>
*/

using UnityEngine;
using UnityEngine.Events;
using System;
using System.Runtime.InteropServices;

[Serializable]
public class GstUnityBridgeCroppingParams
{
    [Tooltip("Amount to crop from the left margin")]
    [Range(0, 1)]
    public float m_Left = 0.0F;
    [Tooltip("Amount to crop from the top margin")]
    [Range(0, 1)]
    public float m_Top = 0.0F;
    [Tooltip("Amount to crop from the right margin")]
    [Range(0, 1)]
    public float m_Right = 0.0F;
    [Tooltip("Amount to crop from the bottom margin")]
    [Range(0, 1)]
    public float m_Bottom = 0.0F;
};

[Serializable]
public class GstUnityBridgeSynchronizationParams
{
    [Tooltip("If unchecked, next two items are unused")]
    public bool m_Enabled = false;
    [Tooltip("IP address or host name of the GStreamer network clock provider")]
    public string m_MasterClockAddress = "";
    [Tooltip("Port of the GStreamer network clock provider")]
    public int m_MasterClockPort = 0;
}

[Serializable]
public class GstUnityBridgeDebugParams
{
    [Tooltip("Enable to write debug output to the Unity Editor Console, " +
        "LogCat on Android or gub.txt on a Standalone player")]
    public bool m_Enabled = false;
    [Tooltip("Comma-separated list of categories and log levels as used " +
        "with the GST_DEBUG environment variable. Setting to '2' is normally enough. " +
        "Leave empty to disable GStreamer debug output.")]
    public string m_GStreamerDebugString = "2";
}

[Serializable]
public class StringEvent : UnityEvent<string> { }

[Serializable]
public class GstUnityBridgeEventParams
{
    [Tooltip("Called when media reaches the end")]
    public UnityEvent m_OnFinish;
    [Tooltip("Called when GStreamer reports an error")]
    public StringEvent m_OnError;
}

public class GstUnityBridgeTexture : MonoBehaviour
{
#if !EXPERIMENTAL
    [HideInInspector]
#endif
    [Tooltip("The output will be written to a texture called '_AlphaTex' instead of the main texture " +
        "(Requires the ExternalAlpha shader)")]
    public bool m_IsAlpha = false;
    [Tooltip("Flip texture horizontally")]
    public bool m_FlipX = false;
    [Tooltip("Flip texture vertically")]
    public bool m_FlipY = false;
    [Tooltip("Play media from the beginning when it reaches the end")]
    public bool m_Loop = false;
    [Tooltip("URI to get the stream from")]
    public string m_URI = "";
    [Tooltip("Zero-based index of the video stream to use (-1 disables video)")]
    public int m_VideoIndex = 0;
    [Tooltip("Zero-based index of the audio stream to use (-1 disables audio)")]
    public int m_AudioIndex = 0;

    [Tooltip("Optional material whose texture will be replaced. If None, the first material in the Renderer of this GameObject will be used.")]
    public Material m_TargetMaterial;

    [SerializeField]
    [Tooltip("Leave always ON, unless you plan to activate it manually")]
    public bool m_InitializeOnStart = true;
    private bool m_HasBeenInitialized = false;

    public GstUnityBridgeEventParams m_Events = new GstUnityBridgeEventParams();
    public GstUnityBridgeCroppingParams m_VideoCropping = new GstUnityBridgeCroppingParams();
    public GstUnityBridgeSynchronizationParams m_NetworkSynchronization = new GstUnityBridgeSynchronizationParams();
    public GstUnityBridgeDebugParams m_DebugOutput = new GstUnityBridgeDebugParams();

    private GstUnityBridgePipeline m_Pipeline;
    private Texture2D m_Texture = null;
    private int m_Width = 64;
    private int m_Height = 64;
    private EventProcessor m_EventProcessor = null;
    private GCHandle m_instanceHandle;

    void Awake()
    {
        GStreamer.AddPluginsToPath();
    }

    private static void OnFinish(IntPtr p)
    {
        GstUnityBridgeTexture self = ((GCHandle)p).Target as GstUnityBridgeTexture;

        self.m_EventProcessor.QueueEvent(() =>
        {
            if (self.m_Events.m_OnFinish != null)
            {
                self.m_Events.m_OnFinish.Invoke();
            }
            if (self.m_Loop)
            {
                self.Position = 0;
            }
        });
    }

    private static void OnError(IntPtr p, string message)
    {
        GstUnityBridgeTexture self = ((GCHandle)p).Target as GstUnityBridgeTexture;

        self.m_EventProcessor.QueueEvent(() =>
        {
            if (self.m_Events.m_OnError != null)
            {
                self.m_Events.m_OnError.Invoke(message);
            }
        });
    }

    public void Initialize()
    {
        m_HasBeenInitialized = true;

        m_EventProcessor = GetComponent<EventProcessor>();
        if (m_EventProcessor == null)
        {
            m_EventProcessor = gameObject.AddComponent<EventProcessor>();
        }

        GStreamer.GUBUnityDebugLogPFN log_handler = null;
        if (Application.isEditor && m_DebugOutput.m_Enabled)
        {
            log_handler = (int level, string message) => Debug.logger.Log((LogType)level, "GUB", message);
        }

        GStreamer.Ref(m_DebugOutput.m_GStreamerDebugString.Length == 0 ? null : m_DebugOutput.m_GStreamerDebugString, log_handler);

        m_instanceHandle = GCHandle.Alloc(this);
        m_Pipeline = new GstUnityBridgePipeline(name + GetInstanceID(), OnFinish, OnError, (IntPtr)m_instanceHandle);

        Resize(m_Width, m_Height);

        Material mat = m_TargetMaterial;
        if (mat == null && GetComponent<Renderer>())
        {
            // If no material is given, use the first one in the Renderer component
            mat = GetComponent<Renderer>().materials[0];
        }

        if (mat != null)
        {
            string tex_name = m_IsAlpha ? "_AlphaTex" : "_MainTex";
            mat.SetTexture(tex_name, m_Texture);
            mat.SetTextureScale(tex_name, new Vector2(Mathf.Abs(mat.mainTextureScale.x) * (m_FlipX ? -1F : 1F),
                                                      Mathf.Abs(mat.mainTextureScale.y) * (m_FlipY ? -1F : 1F)));
        }
        else
        if (GetComponent<GUITexture>())
        {
            GetComponent<GUITexture>().texture = m_Texture;
        }
        else
        {
            Debug.LogWarning(string.Format("[{0}] There is no Renderer or guiTexture attached to this GameObject! GstTexture will render to a texture but it will not be visible.", name));
        }

    }

    void Start()
    {
        if (m_InitializeOnStart && !m_HasBeenInitialized)
        {
            Initialize();
            Setup(m_URI, m_VideoIndex, m_AudioIndex);
            Play();
        }
    }

    public void Resize(int _Width, int _Height)
    {
        m_Width = _Width;
        m_Height = _Height;

        if (m_Texture == null)
        {
            m_Texture = new Texture2D(m_Width, m_Height, TextureFormat.RGB24, false);
        }
        else
        {
            m_Texture.Resize(m_Width, m_Height, TextureFormat.RGB24, false);
            m_Texture.Apply(false, false);
        }
        m_Texture.filterMode = FilterMode.Bilinear;
    }

    public void Setup(string _URI, int _VideoIndex, int _AudioIndex)
    {
        m_URI = _URI;
        m_VideoIndex = _VideoIndex;
        m_AudioIndex = _AudioIndex;
        if (m_Pipeline.IsLoaded || m_Pipeline.IsPlaying)
            m_Pipeline.Close();
        m_Pipeline.SetupDecoding(m_URI, m_VideoIndex, m_AudioIndex,
            m_NetworkSynchronization.m_Enabled ? m_NetworkSynchronization.m_MasterClockAddress : null,
            m_NetworkSynchronization.m_MasterClockPort,
            m_VideoCropping.m_Left, m_VideoCropping.m_Top, m_VideoCropping.m_Right, m_VideoCropping.m_Bottom);
    }

    public void Destroy()
    {
        if (m_Pipeline != null)
        {
            m_Pipeline.Destroy();
            m_Pipeline = null;
            GStreamer.Unref();
        }
        m_instanceHandle.Free();
    }

    public void Play()
    {
        m_Pipeline.Play();
    }

    public void Pause()
    {
        m_Pipeline.Pause();
    }

    public void Stop()
    {
        m_Pipeline.Stop();
    }

    public void Close()
    {
        m_Pipeline.Close();
    }

    void OnDestroy()
    {
        Destroy();
    }

    void OnApplicationQuit()
    {
        Destroy();
    }

    public double Duration
    {
        get { return m_Pipeline != null ? m_Pipeline.Duration : 0F; }
    }

    public double Position
    {
        get { return m_Pipeline != null ? m_Pipeline.Position : 0F; }
        set { if (m_Pipeline != null) m_Pipeline.Position = value; }
    }

    public bool IsPlaying
    {
        get { return m_Pipeline != null ? m_Pipeline.IsPlaying : false; }
    }

    void Update()
    {
        if (m_Pipeline == null)
            return;

        Vector2 sz;
        if (m_Pipeline.GrabFrame(out sz))
        {
            Resize((int)sz.x, (int)sz.y);
            if (m_Texture == null)
                Debug.LogError(string.Format("[{0}] The GstTexture does not have a texture assigned and will not paint.", name));
            else
                m_Pipeline.BlitTexture(m_Texture.GetNativeTexturePtr(), m_Texture.width, m_Texture.height);
        }
    }
}