using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DeployMonitor.Views
{
    public partial class WhitelistWindow : Window
    {
        private readonly ObservableCollection<string> _items = new();
        private readonly Action<string> _onChanged;

        /// <summary>
        /// WhitelistWindow 생성자
        /// </summary>
        /// <param name="currentValue">현재 공백 구분 문자열</param>
        /// <param name="onChanged">변경 시 콜백 (공백 구분 문자열)</param>
        public WhitelistWindow(string currentValue, Action<string> onChanged)
        {
            InitializeComponent();
            _onChanged = onChanged;

            // 기존 값 파싱
            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                foreach (var item in currentValue.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = item.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !_items.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        _items.Add(trimmed);
                }
            }

            ItemsList.ItemsSource = _items;
        }

        private void AddItem()
        {
            var text = InputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!_items.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(text);
                NotifyChanged();
            }

            InputBox.Text = "";
            InputBox.Focus();
        }

        private void NotifyChanged()
        {
            _onChanged?.Invoke(string.Join(" ", _items));
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => AddItem();

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddItem();
                e.Handled = true;
            }
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string item)
            {
                _items.Remove(item);
                NotifyChanged();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
