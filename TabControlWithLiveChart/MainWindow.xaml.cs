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
                .X((model, i) => i)                 //use the data point's index as X
                .Y(model => model.Value);           //use the value property as Y

            //lets save the mapper globally.
            Charting.For<MeasureModel>(mapper);

            //the values property will store our values array
            ChartValues = new ChartValues<MeasureModel>();
            ChartValues2 = new ChartValues<MeasureModel>();

            //X labels just show the data point index now that X is index-based.
            IndexFormatter = value => ((int)Math.Round(value)).ToString();

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

            // Pre-populate 100 fixed data points so we have something to
            // scroll over. Both LineSeries share the same X (index), so
            // they overlay on the same axis.
            {
                var rng = new Random(42);
                double t1 = 0, t2 = 0;
                var origin = DateTime.Now;
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

            // Initial window onto the data and the pan-limit / shadow
            // wiring need the AxisX[0].Model + DrawMargin to be live,
            // which only happens after the chart is loaded. Defer the
            // setup to Loaded.
            Chart.Loaded += OnChartLoadedSetupPanLimits;
        }

        // 12 of N visible at a time. The DATA range is [0, ChartValues.Count - 1].
        private double DataMin { get { return 0; } }
        private double DataMax { get { return Math.Max(0, ChartValues.Count - 1); } }
        private const double InitialMinValue = 0;
        private const double InitialMaxValue = 11;

        private bool _panLimitsInstalled;

        private void OnChartLoadedSetupPanLimits(object sender, RoutedEventArgs e)
        {
            if (_panLimitsInstalled) return;
            _panLimitsInstalled = true;

            // Set the initial 12-point view on the live chart axis.
            // (Removed the AxisMin/AxisMax binding in XAML, so this
            // assignment is the single source of truth for the axis.)
            Chart.AxisX[0].MinValue = InitialMinValue;
            Chart.AxisX[0].MaxValue = InitialMaxValue;

            HookPanLimits(Chart, ChartLeftShadow, ChartRightShadow);
        }

        // === Pan limits via PreviewMouseMove ==============================

        private void HookPanLimits(LiveCharts.Wpf.CartesianChart chart,
            System.Windows.Shapes.Rectangle leftShadow,
            System.Windows.Shapes.Rectangle rightShadow)
        {
            var drawMargin = ChartDrawMarginProperty?.GetValue(chart) as Canvas;
            if (drawMargin == null) return;

            // PreviewMouseMove tunnels down — at DrawMargin (where
            // LiveCharts has its PanOnMouseMove bubble handler) we get
            // a chance to mark the event as Handled BEFORE the pan
            // handler runs. With Handled=true, the bubble subscriber
            // is skipped, so the proposed pan never reaches SetRange.
            //
            // The rule (mirroring the hint): take the mouse's data
            // X under the current axis. If it strays more than 0.5
            // units outside [MinValue, MaxValue], treat it as
            // "trying to scroll past where the chart actually has
            // anything to show" and cancel.
            drawMargin.PreviewMouseMove += (s, e) =>
            {
                var axis = chart.AxisX[0];
                if (drawMargin.ActualWidth <= 0) return;
                if (double.IsNaN(axis.MinValue) || double.IsNaN(axis.MaxValue)) return;

                var pos = e.GetPosition(drawMargin);
                var range = axis.MaxValue - axis.MinValue;
                if (range <= 0) return;

                var cursorData = axis.MinValue + (pos.X / drawMargin.ActualWidth) * range;

                if (cursorData < axis.MinValue - 0.5 || cursorData > axis.MaxValue + 0.5)
                {
                    e.Handled = true;
                }
            };

            // Shadows need to be repositioned when the chart's layout
            // box changes, and re-evaluated whenever the visible
            // window slides over the data.
            Action update = () => UpdateShadow(chart, leftShadow, rightShadow);
            chart.AxisX[0].RangeChanged += _ => update();
            chart.SizeChanged += (s, e) => update();
            drawMargin.SizeChanged += (s, e) => update();

            // Initial paint of the shadows. We've just set MinValue /
            // MaxValue above, which queued a render. ContextIdle (3)
            // fires after LiveCharts' Background-priority (4) Tick has
            // sized DrawMargin, so by the time this runs there's a
            // real plot area to anchor the shadow rectangles to.
            chart.Dispatcher.BeginInvoke(update, DispatcherPriority.ContextIdle);
        }

        private void UpdateShadow(LiveCharts.Wpf.CartesianChart chart,
            System.Windows.Shapes.Rectangle leftShadow,
            System.Windows.Shapes.Rectangle rightShadow)
        {
            var drawMargin = ChartDrawMarginProperty?.GetValue(chart) as Canvas;
            if (drawMargin == null || !drawMargin.IsVisible
                || chart.ActualWidth <= 0 || drawMargin.ActualWidth <= 0)
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
            // bottom of the chart so the rectangle also covers the X
            // axis label strip below DrawMargin.
            var shadowHeight = chart.ActualHeight - plotTop;
            if (shadowHeight <= 0) return;

            Canvas.SetLeft(leftShadow, plotLeft);
            Canvas.SetTop(leftShadow, plotTop);
            leftShadow.Height = shadowHeight;

            Canvas.SetLeft(rightShadow, plotLeft + plotWidth - rightShadow.Width);
            Canvas.SetTop(rightShadow, plotTop);
            rightShadow.Height = shadowHeight;

            // Visibility tracks "is there still data to scroll into on
            // this side?". Tiny epsilon so a clamped pan resting on
            // the boundary doesn't leave a permanent shadow.
            var axis = chart.AxisX[0];
            var min = double.IsNaN(axis.MinValue) ? DataMin : axis.MinValue;
            var max = double.IsNaN(axis.MaxValue) ? DataMax : axis.MaxValue;
            const double eps = 1e-3;
            leftShadow.Visibility = min > DataMin + eps ? Visibility.Visible : Visibility.Collapsed;
            rightShadow.Visibility = max < DataMax - eps ? Visibility.Visible : Visibility.Collapsed;
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

        private double _trend;
        private double _trend2;
        public ChartValues<MeasureModel> ChartValues { get; set; }
        public ChartValues<MeasureModel> ChartValues2 { get; set; }
        public Func<double, string> IndexFormatter { get; set; }

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

                    if (ChartValues.Count > 150) ChartValues.RemoveAt(0);
                    if (ChartValues2.Count > 150) ChartValues2.RemoveAt(0);
                }));
            }
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
