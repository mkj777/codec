using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Codec.Models
{
    public partial class ImportFilterItem : ObservableObject
    {
        private readonly Action<ImportFilterItem> _onSelectedChanged;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private bool _isSelected;

        partial void OnIsSelectedChanged(bool value)
        {
            _onSelectedChanged(this);
        }

        public ImportFilterItem(string name, Action<ImportFilterItem> onSelectedChanged)
        {
            _name = name;
            _onSelectedChanged = onSelectedChanged;
        }
    }
}
