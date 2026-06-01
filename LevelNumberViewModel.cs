using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtilsAR
{
    public class LevelNumberViewModel : INotifyPropertyChanged
    {
        private string _section = "1";
        public string section
        {
            get => _section; set { _section = value; OnPropertyChanged(); }
        }
        private bool _walls = true;
        public bool walls
        {
            get => _walls; set { _walls = value; OnPropertyChanged(); }
        }
        private bool _floors = true;
        public bool floors
        {
            get => _floors; set { _floors = value; OnPropertyChanged(); }
        }
        private bool _ceilings = true;
        public bool ceilings
        {
            get => _ceilings; set { _ceilings = value; OnPropertyChanged(); }
        }
        private bool _instances = true;
        public bool instances
        {
            get => _instances; set { _instances = value; OnPropertyChanged(); }
        }
        private bool _rooms = true;
        public bool rooms
        {
            get => _rooms; set { _rooms = value; OnPropertyChanged(); }
        }
        private bool _park = true;
        public bool park
        {
            get => _park; set { _park = value; OnPropertyChanged(); }
        }
        private bool _other = true;
        public bool other
        {
            get => _other; set { _other = value; OnPropertyChanged(); }
        }
        private bool _beams = true;
        public bool beams
        {
            get => _beams; set { _beams = value; OnPropertyChanged(); }
        }
        private bool _checkBox8islocked;
        public bool checkBox8islocked
        {
            get => _checkBox8islocked;
            set
            {
                _checkBox8islocked = value;
                OnPropertyChanged();
            }
        }
        private bool _holes = true;
        public bool holes
        {
            get => _holes; set { _holes = value; OnPropertyChanged(); }
        }
        public event EventHandler CloseRequest;
        private void RaiseCloseRequest()
        {
            CloseRequest?.Invoke(this, EventArgs.Empty);
        }
        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

    }
}
