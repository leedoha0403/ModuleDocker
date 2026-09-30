# Child 위젯 디자인 · 테마 규약

ModuleDock에 들어가는 모든 child 위젯(`FeatureName.Widget`)이 따라야 하는 디자인 규칙이다.
`COMPATIBLE_WIDGET_PROCESS_SPEC.md`의 일부로 취급하며, 여기서 "필수"라고 적은 항목은 호환 위젯 체크리스트에 포함된다.

기준 디자인은 **AI Usage Monitor**(민트 accent, 둥근 카드)이고, Host는 이 디자인을 그대로 따른다. 테마는 **시스템(권장) / 다크 / 라이트** 세 가지이며 사용자가 Host 설정(⚙ → 테마)에서 고른다.
child는 Host 안에서 "Host의 일부처럼" 보여야 한다. 위젯마다 색과 글꼴이 다르면 카드 모음이 어수선해진다.

---

## 1. 원칙

1. **테마는 Host가 정한다.** child는 자기 색을 고르지 않고 Host 테마를 상속받는다. (필수)
2. **단독 실행에서는 기존 모습 유지.** 테마 키가 없으면(= Host 밖) child 자신의 기본값을 쓴다. 기본값도 아래 팔레트와 같아야 한다. (필수)
3. **하드코딩 금지.** 색, 글꼴, 모서리 반경을 위젯 코드에 16진수/숫자로 직접 쓰지 않고 테마 키로 참조한다. (필수)
4. **카드의 바깥은 Host 소유.** 카드 배경·테두리·모서리·핀 버튼·포커스 링은 Host가 그린다. child는 카드 **안쪽 콘텐츠**만 그린다. (필수)
5. **정보 밀도는 낮게.** Summary View는 한눈에 읽히는 핵심 수치 위주, 세부는 Detail View로 보낸다.

---

## 2. 테마 전달 방식

Host가 시작할 때 `Application.Resources`에 아래 키를 등록한다. child는 같은 프로세스에서 실행되므로 계약 변경 없이 `DynamicResource`로 읽는다.
값의 출처는 `HostTheme`(`src/Dora.Widget.Host/HostStyles.cs`)이며, 키 이름은 호환성 계약이므로 바꾸지 않는다.

```xml
<TextBlock Text="Claude"
           Foreground="{DynamicResource ModuleDock.Brush.Text}"
           FontFamily="{DynamicResource ModuleDock.Font.Family}"
           FontSize="{DynamicResource ModuleDock.Font.SizeBody}"/>
```

코드에서 읽을 때는 키가 없을 수 있으므로 fallback을 둔다.

```csharp
Brush text = (Brush?)Application.Current?.TryFindResource("ModuleDock.Brush.Text") ?? MyDefaults.Text;
```

`DynamicResource`를 쓰는 이유: Host가 테마를 바꿔도 child가 다시 만들어지지 않고 즉시 따라온다.

### 2.0 테마 모드 (필수 대응)

| 모드 | 동작 |
|---|---|
| 시스템 (기본, 권장) | Windows의 "앱 모드"(설정 → 개인 설정 → 색)를 따른다. Windows에서 바꾸면 실행 중에도 즉시 바뀐다. |
| 다크 | 항상 다크 팔레트 |
| 라이트 | 항상 라이트 팔레트 |

- 선택값은 `host-settings.json`의 `hostWindow.themeMode`(`"System"` / `"Dark"` / `"Light"`)에 저장된다. 테마는 Host 소유이며 child는 모드를 읽거나 바꾸지 않는다.
- 테마가 바뀌면 Host가 `Application.Resources`의 `ModuleDock.*` 값을 **새 (frozen) 객체로 교체**한다. (Application 리소스의 Brush는 WPF가 freeze하므로 제자리에서 색을 바꿀 수 없다.)
  그래서 `DynamicResource`로 참조한 곳은 자동으로 따라오고, **`FindResource`/`TryFindResource`로 한 번 읽어 저장해 둔 Brush나 `StaticResource`는 따라오지 않는다.**
- 키 값을 자기 리소스 키에 복사해 쓰는 child(§4 패턴)는 `ModuleDock.Theme.Token` 키를 `SetResourceReference`로 구독해 두었다가, 값이 바뀌면 다시 복사한다. (키 값은 테마가 바뀔 때마다 달라진다.)
- child는 **다크·라이트 두 팔레트 모두에서 읽혀야 한다.** `Text`/`Muted`/`Accent` 등 키로만 색을 쓰면 자동으로 충족된다.
  고정 색(`White`, `#000000` 등)을 배경·글자에 쓰면 한쪽 테마에서 안 보이므로 금지한다.
- 단독 실행(Host 밖)에서는 키가 없으므로 child 자신의 기본(다크) 값을 쓴다. 단독 앱이 시스템 테마를 따를지는 앱 자유다.

### 2.1 키 목록

| 키 | 종류 | 값 (다크 / 라이트) | 용도 |
|---|---|---|---|
| `ModuleDock.Brush.Background` | Brush | `#171B1D` / `#F3F6F7` | Host 패널 배경 |
| `ModuleDock.Brush.Surface` | Brush | `#101315` / `#FFFFFF` | 위젯 카드 배경 (카드 안쪽 패널이 더 필요할 때) |
| `ModuleDock.Brush.Border` | Brush | `#2A3336` / `#D3DBDF` | 헤어라인 테두리, 구분선 |
| `ModuleDock.Brush.Text` | Brush | `#F4F7F5` / `#1A2124` | 기본 글자 |
| `ModuleDock.Brush.Muted` | Brush | `#A9B3BD` / `#5A6670` | 보조 글자, 라벨, 단위 |
| `ModuleDock.Brush.Accent` | Brush | `#3FDDB2` / `#0E9F7E` | 강조(막대, 선택, 포커스) |
| `ModuleDock.Brush.AccentBack` | Brush | `#0F2A2B` / `#D5F2E9` | accent 아이콘 뒤 배경 |
| `ModuleDock.Brush.Warning` | Brush | `#F2A93B` / `#C77F0A` | 주의 상태 |
| `ModuleDock.Brush.Danger` | Brush | `#D64545` / `#C23B3B` | 오류·위험 상태 |
| `ModuleDock.Font.Family` | FontFamily | `Segoe UI, Malgun Gothic, Segoe UI Symbol` | 모든 글자 |
| `ModuleDock.Font.SizeBody` | double | `12` | 본문, 라벨 |
| `ModuleDock.Font.SizeTitle` | double | `15` | 위젯 제목 (Bold) |
| `ModuleDock.Radius.Card` | CornerRadius | `8` | 카드 안쪽 패널 모서리 |
| `ModuleDock.Padding.Card` | Thickness | `14` | 카드 안쪽 여백 |
| `ModuleDock.Theme.Token` | int | 테마 변경마다 증가 | 값을 복사해 쓰는 child의 변경 감지용 (§2.0) |

Brush는 frozen 상태로 공유되므로 child가 수정하면 안 된다. 테마를 따라야 하는 곳에는 `DynamicResource`로 참조하고, 변형(투명도 등)이 필요하면 `Opacity`를 쓴다. `Clone()`하거나 코드에서 값을 읽어 저장한 Brush는 테마를 따라오지 않는다.

---

## 3. 디자인 규칙

### 3.1 글꼴
- 모든 텍스트는 `ModuleDock.Font.Family`를 쓴다. 한글은 Malgun Gothic으로 대체된다.
- 크기는 `SizeBody`(12)와 `SizeTitle`(15, Bold) 두 단계가 기본이다.
  큰 수치(핵심 지표)는 20~28 사이에서 쓸 수 있지만 Bold 한 가지 굵기만 쓴다.
- 자체 글꼴 파일을 묶어 배포하지 않는다. (AI Usage Monitor가 단독 실행 시 쓰는 Spoqa Han Sans는 Host 안에서는 쓰지 않는다.)

### 3.2 색
- 배경 위 글자는 `Text`, 설명·단위·라벨은 `Muted`만 쓴다. 회색을 새로 만들지 않는다.
- `Accent`는 한 카드에서 **한 가지 의미**(현재 선택, 진행 막대 등)에만 쓴다. 여기저기 칠하지 않는다.
- 상태색은 의미가 정해져 있다: 정상=`Accent`, 주의=`Warning`, 오류=`Danger`. 위젯이 자기 상태색을 새로 정하지 않는다.
- 카드 안쪽에 패널이 필요하면 `Surface`, 구분선은 `Border` 1px.

### 3.3 모양
- 모서리는 `Radius.Card`(8) 하나만 쓴다. 안쪽 요소(칩, 막대)는 4 이하로 작게 한다.
- 그림자, 그라데이션, 불투명도 애니메이션은 쓰지 않는다. 평면 + 헤어라인이 기준이다.
- 안쪽 여백은 `Padding.Card`(14)를 기준으로 하고, 요소 사이 간격은 4의 배수(4/8/12)로 맞춘다.
- 아이콘은 Segoe MDL2 Assets 또는 텍스트 기호를 쓴다. 이미지 아이콘은 배경이 투명한 단색이어야 한다.

### 3.4 크기 (Layout Profile과 연결)
- `NaturalSize` / `CompactSize` / `CollapsedSize`는 위 여백·글꼴 크기를 기준으로 계산한다. 테마가 바뀌어도 넘치지 않게 `MinHeight` 여유를 둔다.
- `CollapsedSize`(접힌 상태)는 제목 한 줄 + 핵심 수치 하나까지만 허용한다.

### 3.5 상호작용
- 호버·포커스·핀·드래그 표시는 Host가 담당한다. child는 카드 안에서 자체 호버 효과를 만들지 않는다.
  (버튼 등 child 내부 컨트롤은 `Accent` 테두리 강조 정도만 허용)
- 모달 창(권한 요청 제외)을 띄우지 않는다. 설정·상세는 Detail View로 보낸다.

---

## 4. 단독 실행과의 관계

호환 위젯은 단독 앱 + Host 위젯 두 모드를 모두 지원한다(`COMPATIBLE_WIDGET_PROCESS_SPEC.md` §1).

- **Widget 프로젝트(`FeatureName.Widget`)**: 위 `ModuleDock.*` 키를 사용한다.
- **Presentation 프로젝트(공유 UI)**: 색을 자체 키(예: `AppBrush.Text`)로 참조하고, Widget이 로드될 때 자체 키를 `ModuleDock.*` 키에 연결한다.
  ```xml
  <!-- Widget 쪽에서 Host 테마를 자체 키에 연결 -->
  <SolidColorBrush x:Key="AppBrush.Text" ... />   <!-- 기본값(단독 실행) -->
  ```
  ```csharp
  // Host 안에서만 실행: 키가 있으면 자체 리소스를 Host 값으로 덮어쓴다
  foreach (var (mine, host) in map)
      if (Application.Current.TryFindResource(host) is { } v) Application.Current.Resources[mine] = v;
  ```
- 이렇게 하면 같은 Presentation 코드가 단독 실행에서는 자기 팔레트, Host 안에서는 Host 팔레트로 보인다.
- 단독 실행 기본 팔레트는 §2.1 값과 같게 유지한다. (AI Usage Monitor는 이미 같은 값이다.)

---

## 5. 체크리스트 (호환 위젯 추가 항목)

```text
[ ] 색·글꼴·모서리·여백을 ModuleDock.* 키로 참조한다 (하드코딩 없음)
[ ] 키가 없을 때(Host 밖) 동일한 기본값으로 동작한다
[ ] 다크·라이트 두 테마 모두에서 글자와 요소가 읽힌다 (고정 색 없음, `DynamicResource` 참조)
[ ] Host 테마를 바꿔도 위젯을 다시 만들지 않고 즉시 반영된다
[ ] 글꼴은 ModuleDock.Font.Family 하나, 크기는 Body/Title 기준
[ ] Accent는 한 카드에서 한 가지 의미로만 사용한다
[ ] 상태색은 Accent(정상) / Warning(주의) / Danger(오류)만 사용한다
[ ] 그림자·그라데이션·자체 글꼴 파일 없음
[ ] 카드 바깥(배경·테두리·모서리·핀)을 그리지 않는다
[ ] Natural/Compact/Collapsed 크기에서 텍스트가 잘리지 않는다
```

---

## 6. 변경 규칙

- `ModuleDock.*` 키 **이름**과 의미는 계약이다. 이름 변경·삭제는 `ContractVersion`을 올릴 때만 한다.
- 키를 **추가**하는 것은 호환을 깨지 않는다. 값 변경(테마 교체·팔레트 추가)은 child 수정 없이 반영되어야 한다.
- child가 필요한 색이 목록에 없으면 위젯에서 새로 만들지 말고 Host에 키 추가를 요청한다.
