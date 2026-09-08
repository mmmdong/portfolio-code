using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace REIW
{
    public class VersionChecker : MonoBehaviour
    {
        public string iosBundleId = "com.company.app"; // iOS 번들 ID
        public string iosStoreUrl = "https://apps.apple.com/app/id123456789"; // iOS 앱스토어 URL
        public string androidStoreUrl = "https://play.google.com/store/apps/details?id=com.company.app"; // 구글 플레이 URL

        public GameObject modalPopup; // 모달 팝업 UI 프리팹
        // public Text modalText; // 팝업 메시지 텍스트
        // public Button yesButton;
        // public Button noButton;

        void Start()
        {
            string currentVersion = Application.version;
            Debug.Log("현재 앱 버전: " + currentVersion);

#if UNITY_IOS && !UNITY_EDITOR
        StartCoroutine(CheckIOSVersion(currentVersion));
#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android는 Play Core 플러그인에서 최신 버전 확인 후 UnitySendMessage로 전달
        // 여기서는 예시로 직접 비교 호출
        CheckAndroidVersion(currentVersion, "1.2.3"); // 최신 버전은 플러그인에서 받아옴
#endif
        }

        IEnumerator CheckIOSVersion(string currentVersion)
        {
            string url = "https://itunes.apple.com/lookup?bundleId=" + iosBundleId;
            UnityWebRequest request = UnityWebRequest.Get(url);
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var json = request.downloadHandler.text;
                // 간단 파싱 (실제 구현은 JSONUtility/Newtonsoft.Json 사용 권장)
                if (json.Contains("\"version\":\""))
                {
                    int start = json.IndexOf("\"version\":\"") + 10;
                    int end = json.IndexOf("\"", start);
                    string latestVersion = json.Substring(start, end - start);

                    if (currentVersion != latestVersion)
                    {
                        ShowUpdatePopup("최신 버전(" + latestVersion + ")이 있습니다. 업데이트 하시겠습니까?", iosStoreUrl);
                    }
                }
            }
        }

        void CheckAndroidVersion(string currentVersion, string latestVersion)
        {
            if (currentVersion != latestVersion)
            {
                ShowUpdatePopup("최신 버전(" + latestVersion + ")이 있습니다. 업데이트 하시겠습니까?", androidStoreUrl);
            }
        }

        void ShowUpdatePopup(string message, string storeUrl)
        {
            Debug.LogError(message);
            modalPopup.SetActive(true);
            // modalText.text = message;
            //
            // yesButton.onClick.RemoveAllListeners();
            // yesButton.onClick.AddListener(() => { Application.OpenURL(storeUrl); });
            //
            // noButton.onClick.RemoveAllListeners();
            // noButton.onClick.AddListener(() => { modalPopup.SetActive(false); });
        }
    }
}


