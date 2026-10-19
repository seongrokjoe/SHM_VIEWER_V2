# Single Load 로컬 테스트 샘플

샘플 DAT와 대응 `.dat.schema.json`을 같은 작업 폴더에 복사해서 사용하세요. 저장은 선택한 DAT를 갱신하며 직전 파일을 백업합니다.

| DAT | 대응 구조 파일 | 헤더 사용 시 Struct | 용도 |
|---|---|---|---|
| single_load.dat | single_load.dat.schema.json | SingleLoadSample | 추가·삭제·복구·이름·타입·값·정렬 검증 |
| large_20000.dat | large_20000.dat.schema.json | SingleLoad20000 | 구조체 배열 20,000개 |
| large_100000.dat | large_100000.dat.schema.json | SingleLoad100000 | 구조체 배열 100,000개 |

## 열기

1. `dotnet run --project ShmViewer/ShmViewer.csproj`로 실행합니다.
2. **DAT Diff → Single Load → 버전1**을 선택합니다.
3. 버전1 DAT 경로에 `single_load.dat`를 지정하고 **자동 구조 파일 사용**을 켠 뒤 **Load**를 누릅니다. 헤더 없이도 열립니다.
4. 헤더로 열려면 자동 구조 파일 사용을 끄고 `single_load.h`, 표의 Struct, Pack `8`을 지정합니다. 구조를 수정한 DAT를 다시 열 때는 자동 구조 파일을 사용하세요.

## 편집 확인 순서

1. 트리 화살표로 `items`와 `items[0]`을 펼칩니다. 초기 값은 `nSpare=10/11/12`, `quantity=100/200/300`, `tail=123456`입니다.
2. `items[0].nSpare` 선택 → **아래에 추가** → 이름 `RefillOn` 입력 → 타입 더블클릭 → `short` 선택 → **입력 적용**. 확인창에서 배열 3개에 적용됨을 확인합니다. 배열 길이는 3이고 새 멤버 값은 0입니다.
3. `items[0]` 선택 → **아래에 추가 → 입력 적용**. 배열은 4개가 되고 기존 원소 값은 유지되며 새 원소는 기본값입니다.
4. `items[1]` 선택 → **삭제**에서 취소 후 재시도하여 동의합니다. 삭제 행의 회색 음영·취소선·오프셋 `—`와 나머지 인덱스 재배치를 확인합니다. 삭제 행을 선택하여 **복구**하면 값과 위치가 복구됩니다.
5. `nSpare` 이름을 더블클릭하여 `spareCode`로 변경합니다. 대응하는 모든 배열 원소 이름이 바뀝니다. `quantity` 타입을 `unsigned char`로 바꾸면 300의 변환 손실 확인창이 나옵니다. 취소/동의 및 실행 취소를 확인합니다.
6. 루트 선택 → **첫 하위 추가** → 이름 `extra` → 타입 더블클릭 → **참조 헤더 관리**에서 `single_load.h` 추가 → **참조 타입 찾기** → `Extra::Info` 선택 → 적용. 기존 DAT에서 사용하지 않았던 구조체도 추가됩니다. `long[10]`, `char[20]`, `Extra::Counter`도 입력할 수 있습니다.
7. **DAT 저장 · 원본 백업** 후 자동 구조 파일로 다시 Load하여 변경한 구조·값·크기·오프셋이 유지되는지 확인합니다. 저장 후에도 현재 세션에서는 삭제 행을 복구할 수 있지만, 재로드하면 삭제 이력은 사라집니다. 원본 헤더는 바뀌지 않습니다.
8. 대용량 파일에서 `items`를 펼쳐 중간으로 스크롤하고 원소 및 하위 멤버의 추가·삭제·복구, 실행 취소를 확인합니다. 긴 작업은 상단 **취소**로 중단할 수 있습니다.

행을 우클릭해 추가·이름 변경·타입 변경·삭제·복구 메뉴를 사용할 수도 있습니다. 행 선택 상태에서 `Insert`는 아래에 추가, `Delete`는 삭제입니다. 이름 입력 중에는 `Enter`로 적용하고 `Esc`로 취소합니다.

## 원본 샘플 재생성

저장 테스트로 수정한 샘플을 초기화하려면 저장소 루트에서 아래 명령을 실행합니다. 위 표의 DAT/구조 파일 3쌍을 덮어씁니다.

```powershell
dotnet run --project tools/GenerateSingleLoadSamples -- TestHeaders/SingleLoad --overwrite
```
