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
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Events;
using LiveCharts.Wpf;
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

            // Pre-populate 100 data points so the pan-limit feature
            // has something to scroll over. Both LineSeries share the
            // collections, so Chart and Chart2 show the same data.
            {
                var rng = new Random(42);
                var origin = DateTime.Now;
                double t1 = 0, t2 = 0;
                for (var i = 0; i < 100; i++)
                {
                    t1 += rng.Next(-8, 10);
                    t2 += rng.Next(-8, 10);
                    var when = origin.AddSeconds(i);
                    ChartValues.Add(new MeasureModel { DateTime = when, Value = t1 });
                    ChartValues2.Add(new MeasureModel { DateTime = when, Value = t2 });
                }
                _trend = t1;
                _trend2 = t2;
            }

            // Initial view: first 12 of 100 points. The chart's axis
            // MinValue/MaxValue are now driven directly by user pan
            // (and by our clamping logic), no longer by the AxisMin/
            // AxisMax VM properties.
            var first12Min = ChartValues[0].DateTime.Ticks;
            var first12Max = ChartValues[11].DateTime.Ticks;
            Chart.AxisX[0].MinValue = first12Min;
            Chart.AxisX[0].MaxValue = first12Max;
            Chart2.AxisX[0].MinValue = first12Min;
            Chart2.AxisX[0].MaxValue = first12Max;

            InstallPanLimits(Chart, ChartLeftShadow, ChartRightShadow);
            InstallPanLimits(Chart2, Chart2LeftShadow, Chart2RightShadow);
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

        // AxisCore.BotLimit / TopLimit are `internal` in LiveCharts
        // 0.9.7, so we can't reach them with a normal member access
        // from this assembly — same situation as Freq / IsPanning /
        // DrawMargin. Grab them once via reflection.
        private static readonly PropertyInfo AxisCoreBotLimitProperty =
            typeof(LiveCharts.AxisCore)
                .GetProperty("BotLimit", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly PropertyInfo AxisCoreTopLimitProperty =
            typeof(LiveCharts.AxisCore)
                .GetProperty("TopLimit", BindingFlags.NonPublic | BindingFlags.Instance);

        private static double GetBotLimit(LiveCharts.AxisCore model)
        {
            if (model == null || AxisCoreBotLimitProperty == null) return double.NaN;
            return (double)AxisCoreBotLimitProperty.GetValue(model);
        }

        private static double GetTopLimit(LiveCharts.AxisCore model)
        {
            if (model == null || AxisCoreTopLimitProperty == null) return double.NaN;
            return (double)AxisCoreTopLimitProperty.GetValue(model);
        }

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

        // === Pan limits + side shadows =====================================

        private void InstallPanLimits(CartesianChart chart, Rectangle leftShadow, Rectangle rightShadow)
        {
            var axis = chart.AxisX[0];

            // Clamp pan so the axis never moves outside the data
            // range. Axis.SetRange (the path Pan/Zoom both take)
            // raises PreviewRangeChanged synchronously BEFORE
            // assigning MaxValue/MinValue, and respects pe.Cancel —
            // so we cancel out-of-range proposals and re-apply a
            // clamped range via the MinValue/MaxValue setters, which
            // only fire UpdateChart (no PreviewRangeChanged), giving
            // us a one-shot clamp without recursion.
            //
            // We also refresh the shadows from inside this hook
            // because when ClampPan cancels the pan, RangeChanged
            // doesn't fire and the shadow state would otherwise stay
            // stale.
            Action update = () => UpdateShadow(chart, axis, leftShadow, rightShadow);
            axis.PreviewRangeChanged += pe =>
            {
                ClampPan(pe, axis);
                update();
            };

            // Shadows need to be repositioned whenever the chart's
            // layout box changes, and re-evaluated for visibility
            // whenever the visible window slides over the data.
            axis.RangeChanged += e => update();
            chart.SizeChanged += (s, e) => update();

            // ContextIdle (3) is lower-priority than the Background (4)
            // that LiveCharts' DispatcherTimer uses for its Tick, so
            // this fires AFTER the first Tick has run and sized
            // DrawMargin — without that ordering, the initial update
            // would see drawMargin.ActualWidth == 0 and early-return,
            // leaving the shadows hidden until the user resized or
            // panned.
            chart.Loaded += (s, e) =>
                chart.Dispatcher.BeginInvoke(update, DispatcherPriority.ContextIdle);

            // DrawMargin's actual size only becomes valid after
            // LiveCharts' first layout pass, which can happen after
            // chart.Loaded. Hooking the inner Canvas's own SizeChanged
            // means we catch that 0 → real-size transition and
            // re-place the shadows then.
            var drawMargin = ChartDrawMarginProperty?.GetValue(chart) as FrameworkElement;
            if (drawMargin != null)
            {
                drawMargin.SizeChanged += (s, e) => update();
            }
        }

        private static void ClampPan(PreviewRangeChangedEventArgs pe, Axis axis)
        {
            if (axis?.Model == null) return;

            var dataMin = GetBotLimit(axis.Model);
            var dataMax = GetTopLimit(axis.Model);

            // Skip clamping until LiveCharts has actually computed a
            // valid data range. Before the first Update tick the limits
            // can be NaN, infinite, or both zero — clamping against
            // those values would collapse the axis to [0, 0] on the
            // very first pan and look like "scroll is dead".
            if (double.IsNaN(dataMin) || double.IsNaN(dataMax)
                || double.IsInfinity(dataMin) || double.IsInfinity(dataMax)
                || dataMax <= dataMin)
            {
                return;
            }

            var min = pe.PreviewMinValue;
            var max = pe.PreviewMaxValue;
            var width = max - min;

            var newMin = min;
            var newMax = max;

            if (newMin < dataMin)
            {
                newMin = dataMin;
                newMax = Math.Min(newMin + width, dataMax);
            }
            else if (newMax > dataMax)
            {
                newMax = dataMax;
                newMin = Math.Max(newMax - width, dataMin);
            }

            if (newMin == min && newMax == max) return;

            pe.Cancel = true;
            axis.MinValue = newMin;
            axis.MaxValue = newMax;
        }

        private static void UpdateShadow(CartesianChart chart, Axis axis,
            Rectangle leftShadow, Rectangle rightShadow)
        {
            var drawMargin = ChartDrawMarginProperty?.GetValue(chart) as Canvas;
            if (drawMargin == null || !drawMargin.IsVisible
                || chart.ActualWidth == 0 || drawMargin.ActualWidth == 0)
            {
                leftShadow.Visibility = Visibility.Collapsed;
                rightShadow.Visibility = Visibility.Collapsed;
                return;
            }

            Point plotTopLeft;
            try
            {
                plotTopLeft = drawMargin.TransformToAncestor(chart).Transform(new Point(0, 0));
            }
            catch
            {
                return;
            }

            var plotLeft = plotTopLeft.X;
            var plotTop = plotTopLeft.Y;
            var plotWidth = drawMargin.ActualWidth;

            // Vertical span: from the top of the plot area down to the
            // bottom of the chart, so the shadow covers the X axis
            // label strip too (as requested).
            var shadowHeight = chart.ActualHeight - plotTop;
            if (shadowHeight <= 0) return;

            Canvas.SetLeft(leftShadow, plotLeft);
            Canvas.SetTop(leftShadow, plotTop);
            leftShadow.Height = shadowHeight;

            Canvas.SetLeft(rightShadow, plotLeft + plotWidth - rightShadow.Width);
            Canvas.SetTop(rightShadow, plotTop);
            rightShadow.Height = shadowHeight;

            var dataMin = GetBotLimit(axis.Model);
            var dataMax = GetTopLimit(axis.Model);
            var min = double.IsNaN(axis.MinValue) ? dataMin : axis.MinValue;
            var max = double.IsNaN(axis.MaxValue) ? dataMax : axis.MaxValue;

            // Tiny epsilon so floating-point inaccuracy doesn't leave a
            // permanent shadow glued to the edge after a clamped pan.
            const double eps = 1e-3;
            leftShadow.Visibility = min > dataMin + eps ? Visibility.Visible : Visibility.Collapsed;
            rightShadow.Visibility = max < dataMax - eps ? Visibility.Visible : Visibility.Collapsed;
        }

        // === Visibility tracking ==========================================

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
