// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class FPSCounter : MonoBehaviour
    {
        [Header("Display Settings")]
        public bool showFPS = true;
        public Color textColor = Color.green;
        public int fontSize = 24;
        [Range(0.1f, 2f)]
        public float updateInterval = 0.5f;

        private float m_FPS;
        private float m_AccumulatedTime;
        private int m_FrameCount;
        private GUIStyle m_Style;

        void Start()
        {
            m_Style = new GUIStyle();
            m_Style.fontSize = fontSize;
            m_Style.normal.textColor = textColor;
            m_Style.alignment = TextAnchor.UpperLeft;
        }

        void Update()
        {
            m_AccumulatedTime += Time.unscaledDeltaTime;
            m_FrameCount++;

            if (m_AccumulatedTime >= updateInterval)
            {
                m_FPS = m_FrameCount / m_AccumulatedTime;
                m_AccumulatedTime = 0f;
                m_FrameCount = 0;
            }
        }

        void OnGUI()
        {
            if (!showFPS)
                return;

            if (m_Style == null)
            {
                m_Style = new GUIStyle();
                m_Style.fontSize = fontSize;
                m_Style.normal.textColor = textColor;
                m_Style.alignment = TextAnchor.UpperLeft;
            }

            m_Style.normal.textColor = textColor;
            m_Style.fontSize = fontSize;

            string text = string.Format("FPS: {0:F1}", m_FPS);
            GUI.Label(new Rect(10, 10, 200, 30), text, m_Style);
        }
    }
}
