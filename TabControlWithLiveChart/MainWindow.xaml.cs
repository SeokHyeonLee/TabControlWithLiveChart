using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using LiveCharts;
using LiveCharts.Configurations;
using TabControlWithLiveChart.Annotations;

namespace TabControlWithLiveChart
{
    public class MeasureModel
    {
        public DateTime DateTime { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// MainWindow.xaml 的互動邏輯
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public MainWindow()
        {
            InitializeComponent();

            //To handle live data easily, in this case we built a specialized type
            //the MeasureModel class, it only contains 2 properties
            //DateTime and Value
            //We need to configure LiveCharts to handle MeasureModel class
            //The next code configures MeasureModel  globally, this means
            //that LiveCharts learns to plot MeasureModel and will use this config every time
            //a IChartValues instance uses this type.
            //this code ideally should only run once
            //you can configure series in many ways, learn more at 
            //http://lvcharts.net/App/examples/v1/wpf/Types%20and%20Configuration

            var mapper = Mappers.Xy<MeasureModel>()
                .X(model => model.DateTime.Ticks)   //use DateTime.Ticks as X
                .Y(model => model.Value);           //use the value property as Y

            //lets save the mapper globally.
            Charting.For<MeasureModel>(mapper);

            //the values property will store our values array
            ChartValues = new ChartValues<MeasureModel>();
            ChartValues2 = new ChartValues<MeasureModel>();

            //lets set how to display the X Labels
            DateTimeFormatter = value => new DateTime((long)value).ToString("mm:ss");

            //AxisStep forces the distance between each separator in the X axis
            AxisStep = TimeSpan.FromSeconds(1).Ticks;
            //AxisUnit forces lets the axis know that we are plotting seconds
            //this is not always necessary, but it can prevent wrong labeling
            AxisUnit = TimeSpan.TicksPerSecond;

            SetAxisLimits(DateTime.Now);

            //The next code simulates data changes every 300 ms

            IsReading = false;

            DataContext = this;
        }

        private double _axisMax;
        private double _axisMin;
        private double _trend;
        private double _trend2;
        private volatile bool _isChartTabSelected = true;
        public ChartValues<MeasureModel> ChartValues { get; set; }
        public ChartValues<MeasureModel> ChartValues2 { get; set; }
        public Func<double, string> DateTimeFormatter { get; set; }
        public double AxisStep { get; set; }
        public double AxisUnit { get; set; }

        public double AxisMax
        {
            get { return _axisMax; }
            set
            {
                _axisMax = value;
                OnPropertyChanged("AxisMax");
            }
        }
        public double AxisMin
        {
            get { return _axisMin; }
            set
            {
                _axisMin = value;
                OnPropertyChanged("AxisMin");
            }
        }

        public bool IsReading { get; set; }

        // Bound (OneWayToSource) to the chart TabItem's IsSelected so the
        // background producer can pause while the chart is not in the visual
        // tree. If we keep mutating ChartValues while the chart is unloaded,
        // LiveCharts' ChartUpdater can latch IsUpdating=true on a render that
        // never completes, and every subsequent CollectionChanged is then
        // short-circuited by the `if (IsUpdating && !force) return;` guard.
        public bool IsChartTabSelected
        {
            get { return _isChartTabSelected; }
            set
            {
                if (_isChartTabSelected == value) return;
                _isChartTabSelected = value;
                OnPropertyChanged("IsChartTabSelected");
            }
        }

        private void Read()
        {
            var r = new Random();

            while (IsReading)
            {
                Debug.WriteLine($"hi");
                Thread.Sleep(150);

                // Don't push into ChartValues while the chart is off-screen.
                // The chart is unloaded from the visual tree on tab change, and
                // a render queued mid-flight will never complete, leaving the
                // Updater stuck with IsUpdating=true forever.
                if (!IsChartTabSelected) continue;

                var now = DateTime.Now;

                _trend += r.Next(-8, 10);
                _trend2 += r.Next(-8, 10);

                ChartValues.Add(new MeasureModel
                {
                    DateTime = now,
                    Value = _trend
                });

                ChartValues2.Add(new MeasureModel
                {
                    DateTime = now,
                    Value = _trend2
                });

                SetAxisLimits(now);

                //lets only use the last 150 values
                if (ChartValues.Count > 150) ChartValues.RemoveAt(0);
                if (ChartValues2.Count > 150) ChartValues2.RemoveAt(0);
            }
        }

        private void OnChartLoaded(object sender, RoutedEventArgs e)
        {
            // Safety net for the case where a render was already in flight when
            // the user switched tabs: force=true bypasses the IsUpdating guard
            // in ChartUpdater.Run, unsticking the updater so live updates
            // resume after the chart re-enters the visual tree.
            ((LiveCharts.Wpf.CartesianChart)sender).Update(false, true);
        }

        private void SetAxisLimits(DateTime now)
        {
            AxisMax = now.Ticks + TimeSpan.FromSeconds(1).Ticks; // lets force the axis to be 1 second ahead
            AxisMin = now.Ticks - TimeSpan.FromSeconds(10).Ticks; // and 8 seconds behind
        }

        private void InjectStopOnClick(object sender, RoutedEventArgs e)
        {
            IsReading = !IsReading;
            if (IsReading) Task.Factory.StartNew(Read);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
