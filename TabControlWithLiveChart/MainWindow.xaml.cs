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
using System.Windows.Threading;
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
        private volatile bool _isChartVisible = true;
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

        // LiveCharts 0.9.7 has three intertwined defects that combine to
        // produce the "freeze / 2 Hz after tab switch" behavior. All three
        // must be addressed; fixing only one or two still feels broken.
        //
        // (A) Phantom-dispatcher Timer.
        //     NoisyCollection<T>.Add (the base of ChartValues<T>) raises
        //     CollectionChanged synchronously on the caller's thread.
        //     ChartValues.OnChanged then calls Updater.Run() on that same
        //     thread. Run() lazily creates `new DispatcherTimer { ... }`,
        //     which captures Dispatcher.CurrentDispatcher at construction.
        //     Chart.Unloaded sets the Timer to null on tab change, so if the
        //     next ChartValues.Add comes from the ThreadPool thread that
        //     Task.Factory.StartNew gave us, the new Timer is bound to that
        //     thread's dispatcher — which has no message pump. Tick never
        //     fires, IsUpdating stays true, and Run()'s
        //     `if (IsUpdating) return;` guard short-circuits every later
        //     call forever.
        //
        // (B) Latched IsUpdating.
        //     ChartUpdater.UpdaterTick early-returns BEFORE Timer.Stop() /
        //     IsUpdating=false when the chart is invisible, so a Tick that
        //     fires while the chart is off-screen also leaves IsUpdating
        //     latched true.
        //
        // (C) DispatcherPriority inversion.
        //     LiveCharts' internal DispatcherTimer uses default priority
        //     `Background` (4) for Tick delivery. Dispatcher.BeginInvoke
        //     used by a naive UI-thread marshal defaults to `Normal` (9).
        //     With Background-rate Adds beating Background-rate Ticks in
        //     the queue, the Add's Run() short-circuits on IsUpdating=true
        //     while the Tick keeps waiting, so the effective render cadence
        //     drops from 1× Sleep interval to 2× — chart visibly stutters
        //     even when it isn't fully stuck.
        //
        // Fix:
        //   * Marshal every ChartValues mutation to the UI Dispatcher so no
        //     Timer can ever be born on a ThreadPool dispatcher — kills (A).
        //   * Pause the producer while the chart is hidden so no Tick ever
        //     fires invisible — kills (B).
        //   * After mutating the collections, call chart.Update(false,true)
        //     synchronously. Run(_, updateNow=true) routes through
        //     UpdaterTick directly with force=true, which calls Timer.Stop
        //     and resets IsUpdating itself, so the internal DispatcherTimer
        //     is bypassed end-to-end — kills (C).
        //   * On the tab coming back, kick once with restartView=true to
        //     rebuild any stale visual state left by Unloaded.
        private void Read()
        {
            var r = new Random();

            while (IsReading)
            {
                Debug.WriteLine($"hi");
                Thread.Sleep(150);

                if (!_isChartVisible) continue;

                var now = DateTime.Now;
                _trend += r.Next(-8, 10);
                _trend2 += r.Next(-8, 10);
                var trend = _trend;
                var trend2 = _trend2;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isChartVisible) return;

                    ChartValues.Add(new MeasureModel { DateTime = now, Value = trend });
                    ChartValues2.Add(new MeasureModel { DateTime = now, Value = trend2 });

                    SetAxisLimits(now);

                    if (ChartValues.Count > 150) ChartValues.RemoveAt(0);
                    if (ChartValues2.Count > 150) ChartValues2.RemoveAt(0);

                    // Force the render right now, bypassing the
                    // Background-priority DispatcherTimer that otherwise
                    // halves the frame rate against our Normal-priority
                    // Adds. Run(_, updateNow=true) goes through
                    // UpdaterTick directly with force=true, which stops
                    // any pending Tick and clears IsUpdating, so the
                    // updater state stays clean across every cycle.
                    Chart.Update(false, true);
                }));
            }
        }

        private void OnChartIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var visible = (bool)e.NewValue;
            _isChartVisible = visible;
            if (!visible) return;

            var chart = (LiveCharts.Wpf.CartesianChart)sender;
            // restartView=true rebuilds the series visual elements that
            // Chart.Unloaded left in a stale state. Deferred to Render
            // priority so it happens after WPF lays out the freshly
            // re-attached chart and its ActualWidth/Height are valid.
            chart.Dispatcher.BeginInvoke(
                new Action(() => chart.Update(true, true)),
                DispatcherPriority.Render);
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
