# 3D 클라이언트 실행

2026-10-12: 3D game protocol **9**, Login protocol 2. 최신 서버와 함께 실행한다. 2D 클라이언트는 기존 protocol 6을 유지한다.

## 씬과 실행 파일

- Unity 6000.3.7f1에서 `Assets/Scenes/SampleScene.unity`를 연다.
- 지형·시작 위치·클라이언트·입력 진단 컴포넌트·인벤토리 Canvas·EventSystem이 씬에 저장되어 있다.
- `Tools > MyMMORPG > Place Inventory and Input Probe`는 빠진 UI만 추가하며 기존 지형을 다시 생성하지 않는다.
- `Tools > MyMMORPG > Build 3D Windows Client`로 `Builds/Movement3D/MyMMORPGClient_3D.exe`를 만든다.
- 실행 파일과 `_Data`, `UnityPlayer.dll`, `MonoBleedingEdge` 등 빌드 디렉터리를 함께 사용한다.
- `Offline preview`는 DB 없이 씬 지형과 샘플 인벤토리·몬스터를 확인한다. 온라인에서는 서버 지형과 서버의 몬스터 상태를 사용한다.

## 조작

| 입력 | 동작 |
|---|---|
| WASD / 방향키 | XZ 이동 |
| Space | 지상에서 새 입력으로 점프 |
| J / 왼쪽 Ctrl / 하단 ATTACK | 기본 근접 공격 |
| I / Inventory 버튼 | 인벤토리 열기·닫기 |
| Esc | 인벤토리 닫기·채팅 입력 포커스 해제 |
| R | 오프라인 위치 초기화 |

인벤토리 창이 열려 있거나 텍스트 입력 중이면 캐릭터 입력을 차단한다. 창 비활성화·맵 이동 시 대기 입력을 지운다. Windows 접근성 입력처럼 Unity 창 키 이벤트만 전달하는 입력도 지원한다. 매우 짧은 이동 입력은 다음 고정 이동 단계까지 보존한다.

## 기본 공격과 하단 HP·MP

J 또는 왼쪽 Ctrl을 새로 누르거나 하단 ATTACK 버튼을 클릭하면 검을 휘두른다. 누른 채 유지하는 입력은 연속 공격으로 처리하지 않는다. 서버는 0.5초 간격, 전방 3m·높이 차이 1.5m 이내, 지형에 가려지지 않은 가장 가까운 몬스터 한 마리에 20 피해를 적용한다. 슬라임은 HP 60이며 세 번 적중하면 사망하고 5초 후 재생성된다. 기본 공격의 MP 소모는 0이다.

검 표시는 입력 즉시 재생하지만 피해·HP·사망은 서버 응답으로 확정한다. 인벤토리·채팅·창 비활성화·Map 전환 중에는 공격을 차단한다. 하단 HP·MP 숫자와 게이지는 인벤토리를 닫아도 표시되며 서버의 캐릭터별 DB 상태를 사용한다. 물약 사용이 확정되면 두 UI가 함께 갱신된다. 씬의 `Player Vitals HUD`에서 배치를 조정할 수 있다.

패킷이 변경되어 서버와 3D 클라이언트를 모두 version 9로 빌드해야 한다. DB 스키마는 그대로 사용한다. [서버 판정과 검증 기록](../MyMMORPGServer/docs/BASIC_ATTACK_3D.md)을 참고한다.

## 인벤토리

장비·소비·설치·기타·캐시의 5개 탭, 탭당 24칸, 샘플 아이템 8종, 수량·설명·메소·HP/MP 표시를 제공한다. 드래그로 같은 탭 안의 슬롯 이동·교환·병합, SORT로 정렬·동일 아이템 병합, 포션 더블클릭 또는 USE로 HP/MP 회복이 가능하다. 회복 수치가 가득 차면 수량을 소모하지 않는다.

온라인에서는 서버가 조작과 수량을 검증하고 **MySQL의 캐릭터별 인벤토리·슬롯 테이블**에 저장한다. 슬롯 변경·포션 소비·HP/MP·revision을 하나의 트랜잭션으로 처리한다. 기존 DB에는 서버 저장소의 `database/init/002_inventory.sql`을 적용해야 한다. DB 변경으로 클라이언트 패킷 형식은 바뀌지 않았다. 오프라인 프리뷰의 조작은 온라인 캐릭터에 반영하지 않는다. 장비 착용·거래·아이템 획득은 후속 기능이다. 스키마 적용과 검증은 [INVENTORY_DATABASE.md](../MyMMORPGServer/docs/INVENTORY_DATABASE.md)를 참고한다.

## 몬스터

3D 맵 100000002/100000003에 서버가 순찰하는 기본 슬라임 3마리를 만든다. 같은 맵의 모든 캐릭터에게 동일 tick의 몬스터 ID·XYZ·방향을 200ms 간격으로 전달한다. 늦게 입장한 캐릭터도 현재 상태를 받는다. Map 변경·재접속 시 이전 몬스터를 제거하고 새 generation의 상태만 적용한다. 기본 공격에 따른 HP 감소·피격 수치·사망·5초 후 재생성을 공유한다. 몬스터의 플레이어 공격과 아이템 드롭은 후속 기능이다.

## 검증

서버 저장소에서 `Tests/NetworkReliability/RunFieldFeatures.ps1`로 DB 없는 인벤토리 규칙·몬스터 공유 TCP 검사를 반복한다. `RunMovement3D.ps1`은 기존 이동과 세 지연 조건 검사다. `RunFull3DIntegration.ps1`은 InventoryDatabase 검증 프로그램과 임시 MySQL, 실제 LoginServer/GameServer·두 Unity Player를 사용해 DB 저장·동시 소비·롤백·연결을 확인한다. 검사 후 이번에 시작한 프로세스·컨테이너만 종료한다.

실제 창 입력을 기록하려면 다음처럼 실행한다. 이 옵션은 입력을 생성하지 않는다.

```powershell
.\Builds\Movement3D\MyMMORPGClient_3D.exe --offline-preview --input-probe
```

`Logs/input-probe.json`에 입력 횟수·점프 ID·좌표·포커스·씬 지형 사용 여부가 기록된다. `--smoke-test` 등의 자동 이동 옵션과 함께 실행하지 않는다. 사람이 길게 키를 누르는 동작, 공중 반복 입력과 누른 채 착지하는 동작은 실제 키보드로 별도 확인할 수 있다.

## 10월 12일 입력·복구 보완

채팅·인벤토리·비활성화·Map 전환 중 눌린 키는 떼기 전까지 이동에 재사용하지 않는다. Esc는 채팅 포커스를 해제한다. 인벤토리 응답이 5초 동안 없거나 저장 오류가 발생하면 변경 조작을 막고 조회만 최대 3회 재시도한다. REFRESH 버튼으로 수동 조회도 가능하며 물약 사용 요청을 자동 재전송하지 않는다. 온라인 상태를 표시한 뒤 오프라인 프리뷰로 전환하면 별도 샘플 인벤토리로 초기화한다.

몬스터는 수신 상태 사이를 보간하고 큰 위치 차이는 즉시 맞추며, 수신이 끊기면 마지막 상태에서 멈춘다. 공통 재질·카메라·슬롯 표시를 재사용해 반복 갱신 비용을 줄였다.

서버의 RunFull3DIntegration.ps1은 실제 슬롯 조작과 두 서버 재시작까지 검증한다. -ManualInput 모드는 테스트 계정으로 창 두 개를 표시하며 자동 이동을 사용하지 않는다. 출력한 검사 디렉터리의 manual-finish 파일이 있으면 종료하며, 실제 점프 기록 없이 완료되면 실패한다. input-probe.json과 input-probe-events.jsonl에 mode=manual인 실제 키 입력 상태를 남긴다.

변경 내용·측정 범위·다음 기능 설계는 [10월 12일 보완 기록](../MyMMORPGServer/docs/FIELD_HARDENING_2026-10-12.md)을 참고한다.
