# LiveCharts 0.9.7 + WPF TabControl: 진짜 원인과 해결책

## TL;DR

WPF `TabControl`에 LiveCharts를 넣고 탭을 한 번이라도 전환했다 돌아오면
차트가 멈추거나, Pan/Zoom이 ~3 Hz로 끊긴다.
6년 이상 [GitHub Issue #599](https://github.com/Live-Charts/Live-Charts/issues/599)에서
보고됐지만 어떤 우회법도 근본 원인을 짚지 못했다.

진짜 원인은 `LiveCharts.Wpf.Components.ChartUpdater`의 `private TimeSpan Freq` 필드가
**base `Chart` 생성자가 박은 default `AnimationsSpeed = 300ms`** 로 박혀 있고,
XAML 속성 변경에도 절대 재동기되지 않는 것이다. `Chart.Unloaded`가 Timer를 null로
만들고, 탭 복귀 후 `Run()`이 그 stale `Freq`로 새 Timer를 만들면 모든 Timer-기반
렌더가 300 ms 간격으로 죽는다.

수정은 **세 줄 짜리 fix 한 개 + 두 줄짜리 보조 가드 두 개**:

1. `MainWindow` 생성자에서 리플렉션으로 `Freq`를 실제 값으로 덮어쓴다.
2. `Read()`의 `ChartValues` 변형을 `Dispatcher.BeginInvoke`로 UI 스레드 마샬링한다.
3. `IsVisibleChanged`로 가시성 추적해 invisible 동안 producer를 일시정지한다.

`TabControlEx`, View 동적 생성, LiveCharts 재컴파일 모두 불필요. MVVM 그대로 유지.

---

## 1. 증상

- 탭 전환 후 차트가 **완전히 멈춤** 또는 **2–3 Hz로 끊김**
- Pan/Zoom이 부드럽지 않고 마우스 드래그에 비해 한참 지연되어 따라옴
- 데이터 자체는 `ChartValues`에 계속 들어가지만 화면에 안 그려짐
- 애니메이션을 끄거나 Dispatcher.Invoke로 감싸는 등의 흔한 우회법으로는 일시적/부분적으로만 완화

기존 issue 스레드에서 제시된 해결책들:

| 제안 | 실효성 | 비고 |
|------|--------|------|
| `TabControlEx`로 visual tree 보존 | △ | 일부 케이스만 해결, MVVM 깨짐, 외부 의존성 |
| `Dispatcher.Invoke`로 `ChartValues.Add` 감싸기 | △ | 데이터 피드의 stuck만 일부 해결, Pan 렉은 그대로 |
| 차트를 코드비하인드에서 동적 재생성 | △ | MVVM 박살, 유지보수 비용 ↑ |
| `Loaded` 이벤트에서 `chart.Update(false, true)` | △ | stuck 한 번 풀어도 다음 사이클에 다시 stuck |
| `Updater.IsUpdating`을 매번 reset | × | 증상 일부 가림, 근본 원인은 안 잡힘 |

전부 **원인이 아니라 증상의 일부**를 가리는 우회였다.

---

## 2. 진짜 원인: stale `Freq`

LiveCharts 0.9.7 소스 코드 그대로의 호출 순서:

### 2-1. `Chart` base ctor가 기본값을 박는다

`WpfView/Charts/Base/Chart.cs`:

```csharp
public Chart()
{
    ...
    SetCurrentValue(AnimationsSpeedProperty, TimeSpan.FromMilliseconds(300));
    SetCurrentValue(TooltipTimeoutProperty,  TimeSpan.FromMilliseconds(800));
    ...
}
```

이 시점에서 `AnimationsSpeed = 300ms`.

### 2-2. `CartesianChart` 파생 ctor가 그 값을 캡처한다

`WpfView/CartesianChart.cs`:

```csharp
public CartesianChart()
{
    var freq = DisableAnimations ? TimeSpan.FromMilliseconds(10) : AnimationsSpeed;
    //                              ↑ default false           ↑ 300ms (base ctor가 방금 셋팅)
    //                              결과 freq = 300ms

    var updater = new Components.ChartUpdater(freq);
    ChartCoreModel = new CartesianChartCore(this, updater);
    ...
}
```

### 2-3. `ChartUpdater` ctor가 `Freq`에 영구 박는다

`WpfView/Components/ChartUpdater.cs`:

```csharp
public ChartUpdater(TimeSpan frequency)
{
    Timer = new DispatcherTimer { Interval = frequency };
    Timer.Tick += OnTimerOnTick;
    Freq = frequency;        // ← private. 이후 이 필드를 셋팅하는 코드 경로가 어디에도 없음.
}
```

### 2-4. XAML 속성은 `Timer.Interval`만 갱신하고 `Freq`는 못 건드린다

XAML에 `DisableAnimations="True"`나 `AnimationsSpeed="0:0:0.15"`를 쓰면
`Chart`의 DP 콜백 `UpdateChartFrequency`가 호출되고, 이 콜백은
`Updater.UpdateFrequency()`를 부른다. 그 구현은:

`WpfView/Components/ChartUpdater.cs`:

```csharp
public override void UpdateFrequency(TimeSpan freq)
{
    Timer.Interval = freq;   // ← Timer.Interval만 갱신
    //                          Freq 필드는 절대 다시 셋팅하지 않음
}
```

**여기가 버그.** `Freq`는 300 ms로 그대로 박혀 있다.

### 2-5. 탭 전환이 Timer 객체 자체를 폐기한다

`WpfView/Charts/Base/Chart.cs`의 `Unloaded` 핸들러:

```csharp
Unloaded += (sender, args) =>
{
    var updater = (Components.ChartUpdater) Model.Updater;
    if (updater.Timer == null) return;
    updater.Timer.Tick -= updater.OnTimerOnTick;
    updater.Timer.Stop();
    updater.Timer.IsEnabled = false;
    updater.Timer = null;     // ← Timer 폐기
};
```

`Timer.Interval`을 안고 있던 `DispatcherTimer` 객체가 사라진다.
그러나 `Freq`는 여전히 300 ms.

### 2-6. 탭 복귀 후 첫 `Run()`이 stale `Freq`로 Timer를 부활시킨다

`WpfView/Components/ChartUpdater.cs`:

```csharp
public override void Run(bool restartView = false, bool updateNow = false)
{
    if (Timer == null)
    {
        Timer = new DispatcherTimer { Interval = Freq };
        //                                       ↑ 300ms (stale)
        Timer.Tick += OnTimerOnTick;
        IsUpdating = false;
    }
    ...
    Timer.Start();
}
```

새 Timer의 Interval = **300 ms**. 사용자가 XAML에 뭐라고 적었든 무관하게
Tick은 300 ms마다 한 번. Pan/Zoom은 `SetRange → Updater.Run()`(force=false)로
이 Timer 경로를 타므로 **렌더 주기 3.3 Hz = 명백한 렉**.

이게 모든 증상의 핵심이다. 다른 자잘한 버그들 — phantom-dispatcher Timer,
invisible-tick IsUpdating latch — 도 이 stuck Timer 위에서 증상으로 드러난다.

---

## 3. 보조 버그 둘

### 3-A. Phantom-dispatcher Timer

`Core40/Helpers/NoisyCollection.cs`의 `Add`는 `CollectionChanged`를
**호출 스레드에서 동기로** 발사한다. `ChartValues.OnChanged`가 그 위에서
`Updater.Run()`을 호출하고, `Run()`이 `Timer == null`을 보면 즉석에서
`new DispatcherTimer { Interval = Freq }`를 만든다.

`DispatcherTimer` 생성자는 **`Dispatcher.CurrentDispatcher`를 캡처**한다.
원본 데모처럼 `Task.Factory.StartNew(Read)`로 띄운 ThreadPool 스레드가
`ChartValues.Add`를 부르면, 그 시점 `CurrentDispatcher`는 ThreadPool 스레드의
**메시지 펌프 없는 임시 Dispatcher**다. Tick이 영원히 발사되지 않으니
`IsUpdating = true` 영구 stuck → 차트 사망.

### 3-C. Pan에 mouse capture가 없다

`WpfView/Charts/Base/Chart.cs`는 Pan 처리를 위해 `DrawMargin`에
`MouseDown`/`MouseUp`/`MouseMove`를 직접 붙이는데 **`CaptureMouse()`를
호출하지 않는다**:

```csharp
DrawMargin.MouseDown += OnDraggingStart;   // IsPanning = true
DrawMargin.MouseUp   += OnDraggingEnd;     // IsPanning = false
DrawMargin.MouseMove += PanOnMouseMove;    // if (IsPanning) drag
// CaptureMouse() 호출 없음
```

같은 파일의 ScrollBar 핸들러는 `CaptureMouse`/`ReleaseMouseCapture`를
명시적으로 호출하지만 Pan 경로는 안 한다.

WPF에서 mouse capture가 없으면 `MouseUp`은 cursor가 element 위에 있어야만
발사된다. 따라서 다음 시나리오가 깨진다:

1. 차트 위에서 mouse down → `IsPanning = true`
2. 누른 채로 cursor가 차트 밖으로 이동
3. 차트 밖에서 mouse release → **`DrawMargin.MouseUp`이 발사 안 됨** →
   `IsPanning` 영구 `true`
4. cursor가 다시 차트 안으로 들어옴 → `MouseMove` 발사 → `IsPanning=true`
   조건 통과 → 사용자가 버튼을 누르지 않았는데도 차트가 마우스를 따라옴

### 3-B. Invisible Tick이 IsUpdating을 latch한다

`WpfView/Components/ChartUpdater.cs`의 `UpdaterTick`:

```csharp
private void UpdaterTick(bool restartView, bool force)
{
    var wpfChart = (Chart) Chart.View;

    if (!force && !wpfChart.IsVisible && !wpfChart.IsMocked) return;
    //                                                       ↑ Timer.Stop()과
    //                                                         IsUpdating=false에
    //                                                         도달하기 전에 return

    Chart.ControlSize = ...;
    Timer.Stop();
    Update(restartView, force);
    IsUpdating = false;
    ...
}
```

invisible 상태에서 Tick이 한 번이라도 발사되면 `IsUpdating`이 `true`인 채로
남고, 그 뒤로 모든 `Run()` 호출이 `if (IsUpdating) return;` 가드에 막힌다.

---

## 4. 수정

네 가지 변경. 외부 라이브러리 없음, MVVM 그대로, View 동적 생성 없음.

### 4-1. 핵심: `Freq` 리플렉션 동기화

`MainWindow.xaml.cs`:

```csharp
private static readonly PropertyInfo UpdaterFreqProperty =
    typeof(LiveCharts.Wpf.CartesianChart).Assembly
        .GetType("LiveCharts.Wpf.Components.ChartUpdater")
        ?.GetProperty("Freq", BindingFlags.NonPublic | BindingFlags.Instance);

private void SyncUpdaterFreq()
{
    var updater = Chart.Model?.Updater;
    if (updater == null || UpdaterFreqProperty == null) return;

    var freq = Chart.DisableAnimations
        ? TimeSpan.FromMilliseconds(10)
        : Chart.AnimationsSpeed;
    UpdaterFreqProperty.SetValue(updater, freq);
}
```

`InitializeComponent()` 직후, XAML 속성이 모두 적용된 시점에 **한 번만** 호출한다.
이후 모든 Timer 재생성(=탭 전환 후 매번)은 올바른 frequency를 사용한다.

§2의 진짜 root cause를 직접 잡는 유일한 변경이다.

### 4-2. `ChartValues` 변형을 UI Dispatcher로 마샬링 (§3-A 회피)

```csharp
private void Read()
{
    var r = new Random();
    while (IsReading)
    {
        Thread.Sleep(150);
        if (!_isChartVisible) continue;

        var now = DateTime.Now;
        _trend  += r.Next(-8, 10);
        _trend2 += r.Next(-8, 10);
        var trend  = _trend;
        var trend2 = _trend2;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isChartVisible) return;

            ChartValues.Add(new MeasureModel { DateTime = now, Value = trend  });
            ChartValues2.Add(new MeasureModel { DateTime = now, Value = trend2 });

            SetAxisLimits(now);

            if (ChartValues.Count  > 150) ChartValues.RemoveAt(0);
            if (ChartValues2.Count > 150) ChartValues2.RemoveAt(0);
        }));
    }
}
```

`Add`가 UI 스레드에서 호출되므로, 그 안의 동기 `CollectionChanged →
Updater.Run() → new DispatcherTimer` 경로가 UI Dispatcher를 캡처한다.
Tick은 UI 스레드에서 정상 발사된다.

### 4-3. invisible 동안 producer 일시정지 (§3-B 회피)

```xml
<lvc:CartesianChart x:Name="Chart" ... IsVisibleChanged="OnChartIsVisibleChanged" />
```

```csharp
private volatile bool _isChartVisible = true;

private void OnChartIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
{
    _isChartVisible = (bool)e.NewValue;
}
```

`Read()`의 두 위치(`Thread.Sleep` 직후, `BeginInvoke` 액션 입구)에서
`_isChartVisible`을 검사해 invisible이면 스킵. 결과:

- invisible 동안 `ChartValues.Add`가 일어나지 않음
- → `Updater.Run()`이 호출되지 않음
- → Timer가 시작되지 않음
- → invisible Tick이 발사되지 않음
- → `IsUpdating` latch가 절대 발생하지 않음

`TabItem.IsSelected`에 바인딩해도 무방하지만, 차트 자체의 `IsVisibleChanged`가
TabControl 외 다른 호스팅(Frame/ContentControl/Visibility 토글 등)까지 커버하므로
더 일반적이다.

### 4-4. Pan에 mouse capture를 직접 걸어준다 (§3-C 회피)

내부 `DrawMargin`을 리플렉션으로 꺼내 `PreviewMouseDown`/`PreviewMouseUp`을
hook. `PreviewMouseDown`은 tunneling이므로 LiveCharts의 bubble 핸들러
`OnDraggingStart`보다 먼저 발사되어, `IsPanning`이 `true`로 바뀌는 시점엔
이미 capture가 잡혀있다.

```csharp
private static readonly PropertyInfo ChartDrawMarginProperty =
    typeof(LiveCharts.Wpf.CartesianChart).Assembly
        .GetType("LiveCharts.Wpf.Charts.Base.Chart")
        ?.GetProperty("DrawMargin", BindingFlags.NonPublic | BindingFlags.Instance);

private void HookMouseCaptureForPan()
{
    var drawMargin = ChartDrawMarginProperty?.GetValue(Chart) as UIElement;
    if (drawMargin == null) return;

    drawMargin.PreviewMouseDown += (s, e) => ((UIElement)s).CaptureMouse();
    drawMargin.PreviewMouseUp   += (s, e) => ((UIElement)s).ReleaseMouseCapture();
}
```

capture가 살아있는 동안에는 cursor가 차트 밖이든 위든 모든 mouse 이벤트가
DrawMargin으로 라우팅되므로 mouse release가 차트 바깥에서 일어나도
`MouseUp` → `OnDraggingEnd` → `IsPanning = false`가 정상 수행된다.

다만 capture가 **`MouseUp`을 거치지 않고** 풀려나가는 경로가 여럿 있다
— 다른 element가 capture를 강탈, 윈도우 비활성화, 포커스 손실, 드래그 중
alt-tab 등. 이 경우 `OnDraggingEnd`가 호출되지 않아 `IsPanning`이 `true`로
latch된다. 사용자가 다시 차트 안으로 들어오면 `PanOnMouseMove`의
`if (!IsPanning) return;` 가드를 그대로 통과해 버튼이 눌리지 않았는데도
차트가 마우스를 따라옴.

`LostMouseCapture` 이벤트는 capture가 풀리는 **모든** 경로에서 발사되므로
여기서 reflection으로 `IsPanning`을 강제로 false로 리셋. 정상 release
경로에서는 `OnDraggingEnd`가 이미 false로 만들었으므로 no-op:

```csharp
drawMargin.MouseUp += (s, e) => ((UIElement)s).ReleaseMouseCapture();

drawMargin.LostMouseCapture += (s, e) =>
{
    ChartIsPanningProperty?.SetValue(Chart, false);
};
```

또한 capture release는 **bubble `MouseUp`** 에서 수행한다. 이 시점은
LiveCharts의 `OnDraggingEnd`가 이미 `IsPanning=false`를 수행한 뒤이므로
타이밍 이슈가 없다 (LiveCharts가 chart ctor에서 먼저 구독, 우리는
`MainWindow` ctor에서 나중에 구독 → bubble 단계에서 `OnDraggingEnd`가 먼저
발사).

---

## 5. 검증

| 시나리오 | 패치 전 | 패치 후 |
|---------|---------|---------|
| 최초 실행 후 데이터 피드 | OK | OK |
| 최초 실행 후 Pan | OK (Timer.Interval=10 ms) | OK |
| 탭 전환 후 데이터 피드 | 멈춤 또는 ~3 Hz | OK (원래 6.67 Hz) |
| 탭 전환 후 Pan | **~3 Hz 끊김** | OK (~100 Hz) |
| 탭 N번 왕복 후 모든 동작 | 누적 악화 | 매 회 동일 |
| 누른 채 차트 밖에서 release → 재진입 | **차트가 마우스를 따라옴** | OK (capture로 외부 release 정상 처리) |
| 드래그 중 capture 강탈/포커스 손실/alt-tab 후 재진입 | **차트가 마우스를 따라옴** | OK (LostMouseCapture backstop) |

---

## 6. 변경된 파일

| 파일 | 변경 내용 |
|------|-----------|
| `TabControlWithLiveChart/MainWindow.xaml` | 차트에 `x:Name`, `DisableAnimations="True"`, `Pan="X"`, `IsVisibleChanged` 추가 |
| `TabControlWithLiveChart/MainWindow.xaml.cs` | `SyncUpdaterFreq()`, `OnChartIsVisibleChanged`, `Read()`를 UI 스레드 마샬링 + invisible 가드 |

추가/제거된 외부 의존성 없음. `using System.Reflection;`만 하나 추가.

---

## 7. LiveCharts 원본 수정으로 고치는 법 (참고)

LiveCharts를 fork해서 직접 고친다면 한 줄로 끝난다:

```diff
 public override void UpdateFrequency(TimeSpan freq)
 {
+    Freq = freq;
     Timer.Interval = freq;
 }
```

PR을 보내려 해도 LiveCharts 1.x 리포가 2023-07-17에 archive되었기 때문에
upstream으로 흘려보낼 수는 없다. 본 fix는 그래서 **호출자 쪽 워크어라운드**로
구현되어 있다.
