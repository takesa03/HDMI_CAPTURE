using HDMICapture.Core;
using System.ComponentModel;
using System.Windows.Media;

namespace HDMICapture.App
{
    // 検出一覧の1行分の表示用データ。
    // 一覧は新しい検出が来るたびに作り直さず、この項目を差分更新して選択状態を保つ。
    public class DetectionListItem : INotifyPropertyChanged
    {
        private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
        private static readonly Brush SuspectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)));
        private static readonly Brush ConfirmedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)));

        private string _text = string.Empty;
        private bool _isConfirmed;
        private bool _isAircraftSuspected;

        public DetectionListItem(MeteorDetectionRecord record)
        {
            Record = record;
            _isConfirmed = record.IsConfirmed;
        }

        public MeteorDetectionRecord Record { get; set; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                Raise(nameof(Text));
            }
        }

        public bool IsConfirmed
        {
            get => _isConfirmed;
            set
            {
                if (_isConfirmed == value) return;
                _isConfirmed = value;
                Record.IsConfirmed = value;
                Raise(nameof(IsConfirmed));
                Raise(nameof(TextBrush));
            }
        }

        // 連続した露出で検出が続いている(飛行機の可能性が高い)。
        public bool IsAircraftSuspected
        {
            get => _isAircraftSuspected;
            set
            {
                if (_isAircraftSuspected == value) return;
                _isAircraftSuspected = value;
                Raise(nameof(IsAircraftSuspected));
                Raise(nameof(TextBrush));
            }
        }

        // 確認済み(緑)を最優先し、次に飛行機の疑い(黄)、それ以外は通常色。
        public Brush TextBrush => _isConfirmed ? ConfirmedBrush : _isAircraftSuspected ? SuspectedBrush : NormalBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
