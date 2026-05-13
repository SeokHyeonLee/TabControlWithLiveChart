using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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

            // See docs/LiveCharts-TabSwitchFix.md for the full root-cause
            // analysis. In one line: LiveCharts.Wpf.Components.ChartUpdater
            // captures the base Chart's default AnimationsSpeed (300 ms)
            // into a private `Freq` field that is never re-synced when
            // DisableAnimations / AnimationsSpeed change. After every tab
            // Unloaded → Run() recreates the Timer at that stale 300 ms,
            // and pan/zoom rendering collapses to ~3 Hz. We rewrite the
            // field directly via reflection so subsequent Timer
            // recreations use the actual current frequency.
            SyncUpdaterFreq();

            // LiveCharts' Pan path attaches MouseDown/Move/Up directly to
            // the internal DrawMargin Canvas but never calls
            // CaptureMouse(). If the user presses inside the chart,
            // drags outside, and releases outside, DrawMargin never
            // receives the MouseUp — IsPanning stays true and the next
            // MouseMove after re-entering the chart resumes panning as
            // if the button were still pressed. We capture/release the
            // mouse around the existing Down/Up handlers so MouseUp
            // always reaches DrawMargin even when the cursor is outside.
            HookMouseCaptureForPan();
        }

        private static readonly Type ChartBaseType =
            typeof(LiveCharts.Wpf.CartesianChart).BaseType;

        private static readonly PropertyInfo UpdaterFreqProperty =
            typeof(LiveCharts.Wpf.CartesianChart).Assembly
                .GetType("LiveCharts.Wpf.Components.ChartUpdater")
                ?.GetProperty("Freq", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo ChartDrawMarginProperty =
            ChartBaseType?.GetProperty("DrawMargin", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo ChartIsPanningProperty =
            ChartBaseType?.GetProperty("IsPanning", BindingFlags.NonPublic | BindingFlags.Instance);

        private void SyncUpdaterFreq()
        {
            var updater = Chart.Model?.Updater;
            if (updater == null || UpdaterFreqProperty == null) return;

            var freq = Chart.DisableAnimations
                ? TimeSpan.FromMilliseconds(10)
                : Chart.AnimationsSpeed;
            UpdaterFreqProperty.SetValue(updater, freq);
        }

        private void HookMouseCaptureForPan()
        {
            var drawMargin = ChartDrawMarginProperty?.GetValue(Chart) as UIElement;
            if (drawMargin == null) return;

            // PreviewMouseDown tunnels down, so this fires BEFORE
            // LiveCharts' MouseDown bubble handler (OnDraggingStart) —
            // the capture is in place by the time IsPanning flips to
            // true. With capture, MouseMove keeps routing to DrawMargin
            // even when the cursor leaves the chart's bounds, so pan
            // visibly continues outside the chart.
            drawMargin.PreviewMouseDown += (s, e) =>
            {
                ((UIElement)s).CaptureMouse();
            };

            // Release on the bubble MouseUp (AFTER OnDraggingEnd has
            // already run and set IsPanning=false). LiveCharts
            // subscribed first in the chart ctor, we subscribe later
            // here, so OnDraggingEnd runs first.
            drawMargin.MouseUp += (s, e) =>
            {
                ((UIElement)s).ReleaseMouseCapture();
            };

            // Backstop. Capture can be lost without DrawMargin ever
            // receiving a MouseUp (capture stolen by another element,
            // window deactivated, focus lost, alt-tab while dragging,
            // etc.). In those paths OnDraggingEnd never fires and
            // IsPanning stays latched true — so when the cursor later
            // re-enters the chart the chart "follows" the cursor with
            // the button no longer pressed.
            //
            // LostMouseCapture fires for every capture-loss route
            // (including our own ReleaseMouseCapture above), so we
            // unconditionally reset IsPanning here via reflection. In
            // the normal release path it's a no-op because
            // OnDraggingEnd has already set it to false.
            drawMargin.LostMouseCapture += (s, e) =>
            {
                ChartIsPanningProperty?.SetValue(Chart, false);
            };
        }

        // Tracks whether the chart is currently in the visual tree. Set
        // from IsVisibleChanged on the UI thread, read from the
        // background producer to avoid two further LiveCharts defects:
        //
        //   * ChartValues.Add fires CollectionChanged on the calling
        //     thread; calling it from the ThreadPool while Timer is null
        //     makes Run() build a DispatcherTimer bound to the
        //     ThreadPool thread's pump-less dispatcher (phantom timer →
        //     IsUpdating latched true forever).
        //
        //   * UpdaterTick early-returns when invisible WITHOUT calling
        //     Timer.Stop or IsUpdating=false, so any tick that fires
        //     while the chart is off-screen also latches IsUpdating.
        //
        // Pausing the producer while invisible avoids both: no Run() is
        // ever triggered on a background thread and no Tick ever fires
        // off-screen.
        private volatile bool _isChartVisible = true;

        private void OnChartIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _isChartVisible = (bool)e.NewValue;
        }

        private double _axisMax;
        private double _axisMin;
        private double _trend;
        private double _trend2;
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
                }));
            }
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
