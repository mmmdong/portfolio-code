using GameLogic.Define;

// 캐릭터 카페 서브 컨텐츠 공통 인터페이스 (기획 857473046)
// 메인 Content(ContentEventCharacterCafe)가 합성으로 들고 라이프사이클을 fan-out 한다.
// 각 서브는 자기 데이터 슬라이스(CharacterCafeData.PointData 등)만 읽고,
// 변경은 메인 Content 참조 없이 공통 플로우(EventCharacterCafeHelper.TryUpdateData)를 경유해 핸들러에 위임한다.
public interface ICharacterCafeSubContent
{
    // 데이터 바인딩 (메인 Initialize 시 1회)
    void Initialize(LiveEventData eventData);

    // 오브젝트 1개 상호작용 완료 알림 (흐름도 4-9~4-18). 각 서브가 자기 책임 여부를 판단
    void OnObjectInteractComplete(int objectIdx);

    // 데이터 동기화 후 UI 갱신
    void Refresh();

    void Release();
}
