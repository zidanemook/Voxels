using TMPro;
using Tuntenfisch.Generics;
using UnityEngine;
using Tuntenfisch.World;
using UnityEngine.Serialization;

namespace Tuntenfisch.UI
{
   
    public class UIManager : SingletonComponent<UIManager>
    {
        
        [SerializeField]
        private  TextMeshProUGUI m_frameText;
        
        private float m_deltaTime = 0.0f;
        
        [SerializeField]
        private GameObject m_inGameMenuCanvas;
        
        private bool m_isInUIMode = false;

        public bool IsInUIMode
        {
            get { return m_isInUIMode; }
        }
        
        private void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update()
        {
            GameInfo();

            UpdateUIMode();

        }
        
        public void OnInGameMenu()
        {
            m_inGameMenuCanvas.SetActive(!m_inGameMenuCanvas.activeSelf);

            if (m_inGameMenuCanvas.activeSelf)
            {
                Cursor.lockState = CursorLockMode.None; // 메뉴가 활성화되면 커서 잠금 해제
                Cursor.visible = true; // 커서 보이게
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked; // 메뉴가 비활성화되면 커서 잠금
                Cursor.visible = false; // 커서 숨기기
            }
        }

        private void UpdateUIMode()
        {
            if (m_inGameMenuCanvas.activeSelf || WorldManager.Instance.IsSaving)
                m_isInUIMode = true;
            else
            {
                m_isInUIMode = false;
            }
        }
        
        private void GameInfo()
        {
            // deltaTime을 업데이트
            m_deltaTime += (Time.unscaledDeltaTime - m_deltaTime) * 0.1f;

            // 초당 프레임 수(FPS)를 계산
            float fps = 1.0f / m_deltaTime;
            
            // FPS 값을 문자열로 포맷팅
            string text = string.Format("{0:0.} fps", fps);
            
            // TextMeshProUGUI 컴포넌트의 텍스트를 업데이트
            m_frameText.text = text;
        }
    }
}