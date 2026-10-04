#if UNITY_EDITOR
// Editor 専用: 模擬モード / 遠隔モードの設定。ProjectSettings/ に保存され、ビルドには含まれない。
using UnityEditor;
using UnityEngine;

namespace RasPiLib.UnityGpio
{
    [FilePath("ProjectSettings/UnityGpioSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UnityGpioSettings : ScriptableSingleton<UnityGpioSettings>
    {
        public enum Mode { Mock, Remote }

        public Mode mode = Mode.Mock;
        public string host = "10.89.0.1";
        public int port = 8787;
        public string token = "";

        public void Save() => Save(true);

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/RasPiLib UnityGpio", SettingsScope.Project)
            {
                guiHandler = _ =>
                {
                    var s = instance;
                    EditorGUI.BeginChangeCheck();
                    s.mode = (Mode)EditorGUILayout.EnumPopup("Editor でのモード", s.mode);
                    using (new EditorGUI.DisabledScope(s.mode != Mode.Remote))
                    {
                        s.host = EditorGUILayout.TextField("デーモンのホスト", s.host);
                        s.port = EditorGUILayout.IntField("ポート", s.port);
                        s.token = EditorGUILayout.PasswordField("トークン (tcp_token)", s.token);
                    }
                    EditorGUILayout.HelpBox(s.mode == Mode.Mock
                        ? "模擬モード: 実機なしで動く。入力は Gpio.Mock.SetInput(pin, value)、I2C は Gpio.Mock.I2cHandler で差し込む。"
                        : "遠隔モード: 実機の /etc/unitygpio.conf で tcp_listen と tcp_token を設定し、unitygpiod を再起動しておく。", MessageType.Info);
                    if (EditorGUI.EndChangeCheck()) s.Save();
                },
                keywords = new[] { "GPIO", "Raspberry Pi", "unitygpio" },
            };
        }
    }
}
#endif
