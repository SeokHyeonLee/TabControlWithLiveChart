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
            // analysis. We hook every LiveCharts chart with the same
            // workarounds — the LiveCharts defects are per-instance, so
            // each CartesianChart needs its own:
            //   * SyncUpdaterFreq: rewrite the private Freq field that
            //     would otherwise resurrect a 300 ms Timer after each
            //     tab Unloaded → Run() cycle.
            //   * HookMouseCaptureForPan: install CaptureMouse on the
            //     DrawMargin so MouseUp / drag-outside / focus-loss are
            //     all handled (LiveCharts' Pan path doesn't capture).
            //
            // The methods are written to take a chart parameter so
            // adding another chart anywhere in the app is a one-liner
            // in this ctor.
            ApplyLiveChartsTabSwitchFix(Chart);
            ApplyLiveChartsTabSwitchFix(Chart2);

            _chartVisibility[Chart] = true;     // first tab is selected by default
            _chartVisibility[Chart2] = false;
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

        private static void ApplyLiveChartsTabSwitchFix(LiveCharts.Wpf.CartesianChart chart)
        {
            SyncUpdaterFreq(chart);
            HookMouseCaptureForPan(chart);
        }

        private static void SyncUpdaterFreq(LiveCharts.Wpf.CartesianChart chart)
        {
            var updater = chart.Model?.Updater;
            if (updater == null || UpdaterFreqProperty == null) return;

            var freq = chart.DisableAnimations
                ? TimeSpan.FromMilliseconds(10)
                : chart.AnimationsSpeed;
            UpdaterFreqProperty.SetValue(updater, freq);
        }

        private static void HookMouseCaptureForPan(LiveCharts.Wpf.CartesianChart chart)
        {
            var drawMargin = ChartDrawMarginProperty?.GetValue(chart) as UIElement;
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
            // receiving a MouseUp (capture stolen, window deactivated,
            // focus lost, alt-tab during drag). In those paths
            // OnDraggingEnd never fires and IsPanning stays latched
            // true — when the cursor later re-enters the chart it
            // "follows" with no button pressed. LostMouseCapture fires
            // for every capture-loss route (including our own
            // ReleaseMouseCapture above), so we unconditionally reset
            // IsPanning via reflection. In the normal release path
            // OnDraggingEnd already cleared it, so the assignment is a
            // no-op.
            drawMargin.LostMouseCapture += (s, e) =>
            {
                ChartIsPanningProperty?.SetValue(chart, false);
            };
        }

        // Per-chart visibility tracked from each chart's
        // IsVisibleChanged. The background producer pauses only when
        // EVERY chart is off-screen; if any chart is on screen there is
        // a consumer for the next Add. Stored as
        // ConditionalWeakTable-ish IDictionary so adding charts later
        // never needs a new field.
        private readonly Dictionary<LiveCharts.Wpf.CartesianChart, bool> _chartVisibility
            = new Dictionary<LiveCharts.Wpf.CartesianChart, bool>();

        private bool IsAnyChartVisible
        {
            get
            {
                foreach (var kv in _chartVisibility)
                    if (kv.Value) return true;
                return false;
            }
        }

        private void OnChartIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var chart = (LiveCharts.Wpf.CartesianChart)sender;
            var visible = (bool)e.NewValue;
            _chartVisibility[chart] = visible;

            if (visible)
            {
                // Recover any latched IsUpdating: while this chart was
                // off-screen, ChartValues.Add still triggered its
                // Updater.Run() (because the values are shared with
                // the other chart on the visible tab) and its
                // UpdaterTick early-returned without clearing the
                // flag. force=true bypasses the IsUpdating guard.
                chart.Update(false, true);
            }
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

                if (!IsAnyChartVisible) continue;

                var now = DateTime.Now;
                _trend += r.Next(-8, 10);
                _trend2 += r.Next(-8, 10);
                var trend = _trend;
                var trend2 = _trend2;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsAnyChartVisible) return;

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
