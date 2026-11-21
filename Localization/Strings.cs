using Restocker.Market;

namespace Restocker.Localization;

public enum Language
{
    English = 0,
    Japanese = 1,
    German = 2,
    French = 3,
    Chinese = 4,
    Korean = 5
}

/// <summary>
/// 全 UI 文言の単一ソース。EN/JA/DE/FR/ZH/KO の 6 言語に必ず対応する。
/// 新規文字列を足す時は <see cref="T"/> ヘルパーを使い、6 言語ぶんを 1 行で書くこと。
/// </summary>
public static class Strings
{
    private static Language lang = Language.English;
    public static void SetLanguage(Language l) => lang = l;
    public static Language Current => lang;

    private static string T(string en, string ja, string de, string fr, string zh, string ko) => lang switch
    {
        Language.Japanese => ja,
        Language.German => de,
        Language.French => fr,
        Language.Chinese => zh,
        Language.Korean => ko,
        _ => en
    };

    public static string LanguageNameEnglish => "English";
    public static string LanguageNameJapanese => "日本語";
    public static string LanguageNameGerman => "Deutsch";
    public static string LanguageNameFrench => "Français";
    public static string LanguageNameChinese => "简体中文";
    public static string LanguageNameKorean => "한국어";

    // Fork: manual prefill and Apply show the same warning for the proposed change.
    public static string MaxRaiseSetting => T("Max raise % per change (0 = off)", "1回の値上げ上限 %（0で無効）", "Max. Erhöhung pro Änderung in % (0 = aus)", "Hausse maximale par changement en % (0 = désactivé)", "每次涨价上限 %（0 = 关闭）", "1회 가격 인상 한도 % (0 = 끄기)");
    public static string AutoCostFloorSetting => T("Automatic floor from purchase cost", "購入原価から最低価格を自動設定", "Automatischer Mindestpreis aus Einkaufskosten", "Prix plancher automatique selon le coût d’achat", "按购入成本自动设置底价", "구매 원가 기준 자동 최저가");
    public static string MatchQuality => T("Match quality", "同じ品質で比較", "Qualität abgleichen", "Même qualité", "匹配品质", "동일 품질 비교");
    public static string MatchQualityDefault => T("Match quality by default", "既定で同じ品質のみ比較", "Standardmäßig gleiche Qualität vergleichen", "Comparer la même qualité par défaut", "默认只比较相同品质", "기본적으로 동일 품질만 비교");
    public static string MatchQualityHint => T("Compare HQ with HQ and NQ with NQ. Override each quality beside the item's floor.", "HQ は HQ、NQ は NQ と比較します。アイテムの最低価格の横で品質別に変更できます。", "HQ mit HQ und NQ mit NQ vergleichen. Neben dem Mindestpreis lässt sich jede Qualität einzeln einstellen.", "Compare les offres HQ entre elles et les offres NQ entre elles. Chaque qualité se règle à côté du prix plancher.", "HQ 只与 HQ 比较，NQ 只与 NQ 比较。可在物品底价旁分别设置。", "HQ는 HQ와, NQ는 NQ와 비교합니다. 아이템 최저가 옆에서 품질별로 변경할 수 있습니다.");
    public static string UseDefault => T("Default", "既定", "Standard", "Par défaut", "默认", "기본값");
    public static string NoCompetitor(bool hq) => string.Format(T("no {0} competitor", "{0} の競合なし", "kein {0}-Konkurrenzangebot", "aucune offre concurrente {0}", "没有 {0} 竞争报价", "{0} 경쟁 매물 없음"), hq ? "HQ" : "NQ");
    public static string PriceFloorSettings => T("Price floors (manual applies to HQ and NQ, 0 = auto)", "最低価格（手動はHQ・NQ共通、0 = 自動）", "Mindestpreise (manuell für HQ und NQ, 0 = automatisch)", "Prix planchers (manuel pour HQ et NQ, 0 = auto)", "底价（手动值适用于 HQ 和 NQ，0 = 自动）", "최저가 (수동 값은 HQ와 NQ 공통, 0 = 자동)");
    public static string ResetFloorToAuto => T("Auto", "自動", "Auto", "Auto", "自动", "자동");
    public static string SaleTaxSetting => T("Sales tax %", "販売税率 %", "Verkaufssteuer %", "Taxe sur la vente %", "销售税率 %", "판매세 %");
    public static string MinProfitSetting => T("Minimum profit % of cost", "購入原価に対する最低利益率 %", "Mindestgewinn in % der Kosten", "Bénéfice minimal en % du coût", "最低利润（占成本的百分比）", "원가 대비 최소 이익률 %");
    public static string CostFloorTooltip => T("Floor {0:N0} (cost)", "最低価格 {0:N0}（原価）", "Mindestpreis {0:N0} (Kosten)", "Prix plancher {0:N0} (coût)", "底价 {0:N0}（成本）", "최저가 {0:N0} (원가)");
    public static string ManualFloorTooltip => T("Floor {0:N0} (manual)", "最低価格 {0:N0}（手動）", "Mindestpreis {0:N0} (manuell)", "Prix plancher {0:N0} (manuel)", "底价 {0:N0}（手动）", "최저가 {0:N0} (수동)");
    public static string AutoPriceTooltip => T("Fetch live prices for every retainer and apply the undercut and floor. Changes above either percentage limit are skipped automatically.", "全リテイナーの相場を取得し、値下げ幅と最低価格を適用します。変動率の上限を超えた変更はスキップします。", "Marktpreise für alle Gehilfen abrufen und Unterbietung sowie Mindestpreis anwenden. Änderungen über den Prozentgrenzen werden übersprungen.", "Actualise les prix de tous les servants et applique la sous-cotation et le prix plancher. Les changements dépassant les limites sont ignorés.", "获取所有雇员的实时价格，应用压价和底价。自动跳过超过涨跌幅上限的变更。", "모든 집사의 시세를 확인하고 가격 인하 폭과 최저가를 적용합니다. 변동률 한도를 넘는 변경은 건너뜁니다.");
    public static string ManualPriceTooltip => T("Prefill New for every retainer using the automatic pricing rules. Review marked changes, edit prices, then add to the plan and apply.", "自動価格設定ルールで全リテイナーの新価格を入力します。警告のある変更を確認し、価格を編集してからプランに追加して適用してください。", "Neue Preise für alle Gehilfen nach den automatischen Preisregeln eintragen. Markierte Änderungen prüfen, Preise bearbeiten, zum Plan hinzufügen und anwenden.", "Préremplit les nouveaux prix de tous les servants selon les règles automatiques. Vérifiez les changements signalés, ajustez les prix, ajoutez-les au plan puis appliquez-le.", "按自动定价规则填入所有雇员的新价格。检查标记的变更，编辑价格，然后加入计划并应用。", "자동 가격 규칙으로 모든 집사의 새 가격을 채웁니다. 표시된 변경을 확인하고 가격을 수정한 뒤 계획에 추가하고 적용하세요.");
    public static string SkippedPrice => T("Skipped ({0})", "スキップ（{0}）", "Übersprungen ({0})", "Ignoré ({0})", "已跳过（{0}）", "건너뜀 ({0})");
    public static string PriceWarning(RepriceReason reason, long oldPrice, long newPrice)
    {
        var format = reason == RepriceReason.MaxRaise
            ? T("Raise of {0:0.##}% ({1:N0} → {2:N0}) exceeds the max raise limit. Check before applying.", "{0:0.##}% の値上げ（{1:N0} → {2:N0}）は上限を超えています。適用前に確認してください。", "Erhöhung um {0:0.##}% ({1:N0} → {2:N0}) überschreitet das Limit. Vor dem Anwenden prüfen.", "La hausse de {0:0.##} % ({1:N0} → {2:N0}) dépasse la limite. Vérifiez avant d’appliquer.", "涨幅 {0:0.##}%（{1:N0} → {2:N0}）超过上限。请在应用前确认。", "{0:0.##}% 인상({1:N0} → {2:N0})이 한도를 초과합니다. 적용 전에 확인하세요.")
            : T("Drop of {0:0.##}% ({1:N0} → {2:N0}) exceeds the max drop limit. Check before applying.", "{0:0.##}% の値下げ（{1:N0} → {2:N0}）は上限を超えています。適用前に確認してください。", "Senkung um {0:0.##}% ({1:N0} → {2:N0}) überschreitet das Limit. Vor dem Anwenden prüfen.", "La baisse de {0:0.##} % ({1:N0} → {2:N0}) dépasse la limite. Vérifiez avant d’appliquer.", "跌幅 {0:0.##}%（{1:N0} → {2:N0}）超过上限。请在应用前确认。", "{0:0.##}% 인하({1:N0} → {2:N0})가 한도를 초과합니다. 적용 전에 확인하세요.");
        return string.Format(format, System.Math.Abs(RepriceRules.ChangePercent(oldPrice, newPrice)), oldPrice, newPrice);
    }

    // Fork: sale history capture and display.
    public static string FetchSaleHistory => T("Read sale history when visiting retainers", "リテイナー巡回時に販売履歴を取得", "Verkaufsverlauf bei Gehilfenbesuchen abrufen", "Lire l’historique des ventes lors des visites aux servants", "访问雇员时读取出售记录", "집사 방문 시 판매 내역 조회");
    public static string Buyer => T("Buyer", "購入者", "Käufer", "Acheteur", "购买人", "구매자");
    public static string SaleTime => T("Sale time", "取引日時", "Verkaufszeit", "Date de vente", "交易时间", "판매 일시");

    // Fork: realized profit, stuck listings and sales velocity.
    public static string SalesListings => T("Listings", "出品", "Angebote", "Annonces", "上架记录", "등록 내역");
    public static string SalesPeriod => T("Period", "期間", "Zeitraum", "Période", "时段", "기간");
    public static string SalesAllTime => T("All time", "全期間", "Gesamter Zeitraum", "Tout l’historique", "全部时间", "전체 기간");
    public static string SalesRealizedProfit => T("Realized profit", "実現利益", "Realisierter Gewinn", "Bénéfice réalisé", "已实现利润", "실현 이익");
    public static string SalesProfitTotal => T("Total realized profit  {0}", "実現利益合計  {0}", "Realisierter Gewinn gesamt  {0}", "Bénéfice réalisé total  {0}", "已实现利润合计  {0}", "실현 이익 합계  {0}");
    public static string SalesProfitIncomplete => T("({0:N0} sales with missing cost or proceeds)", "（原価または売上が不明な取引 {0:N0} 件）", "({0:N0} Verkäufe mit fehlenden Kosten oder Erlösen)", "({0:N0} ventes avec coût ou recette manquants)", "（{0:N0} 笔交易缺少成本或收入）", "(원가 또는 판매 대금이 누락된 거래 {0:N0}건)");
    public static string SalesProfitDetails => T("Recorded cost  {0:N2}\nUnits with unknown cost  {1:N0}", "記録原価  {0:N2}\n原価不明の個数  {1:N0}", "Erfasste Kosten  {0:N2}\nStück ohne bekannte Kosten  {1:N0}", "Coût enregistré  {0:N2}\nUnités au coût inconnu  {1:N0}", "已记录成本  {0:N2}\n成本未知的数量  {1:N0}", "기록된 원가  {0:N2}\n원가 미상 수량  {1:N0}");
    public static string SalesNoCostBasis => T("No cost basis was recorded for this sale.", "この取引の原価は記録されていません。", "Für diesen Verkauf wurden keine Kosten erfasst.", "Aucun coût d’achat enregistré pour cette vente.", "此笔交易未记录成本。", "이 거래의 원가가 기록되지 않았습니다.");
    public static string StuckSetting => T("Stuck listing age (days)", "滞留とみなす出品日数", "Alter für Ladenhüter (Tage)", "Âge des annonces stagnantes (jours)", "滞销判定天数", "장기 미판매 기준(일)");
    public static string StuckListings => T("Stuck", "滞留", "Ladenhüter", "En stagnation", "滞销", "장기 미판매");
    public static string StuckEmpty => T("No stuck listings.", "滞留している出品はありません。", "Keine Ladenhüter.", "Aucune annonce stagnante.", "没有滞销商品。", "장기 미판매 등록이 없습니다.");
    public static string StuckRetainer => T("Retainer", "リテイナー", "Gehilfe", "Intendant", "雇员", "리테이너");
    public static string StuckDays => T("Days up", "出品日数", "Tage im Angebot", "Jours en vente", "上架天数", "등록 일수");
    public static string StuckReprices => T("Reprices", "価格変更回数", "Preisänderungen", "Révisions de prix", "改价次数", "가격 변경 횟수");
    public static string DaysShort => T("{0:N0}d", "{0:N0}日", "{0:N0} T.", "{0:N0} j", "{0:N0}天", "{0:N0}일");
    public static string StuckTooltip => T("Up for {0:N1} days, {1:N0} reprices, lowest rival now {2}", "出品から{0:N1}日、価格変更{1:N0}回、競合の最新最安値 {2}", "Seit {0:N1} Tagen im Angebot, {1:N0} Preisänderungen, niedrigster Konkurrenzpreis {2}", "En vente depuis {0:N1} jours, {1:N0} révisions, prix concurrent le plus bas {2}", "已上架 {0:N1} 天，改价 {1:N0} 次，当前竞争对手最低价 {2}", "등록 후 {0:N1}일, 가격 변경 {1:N0}회, 현재 경쟁 최저가 {2}");
    public static string StuckAlert => T("{0} on {1} has been up for {2:N1} days.", "{1}の{0}は出品から{2:N1}日が経過しました。", "{0} bei {1} ist seit {2:N1} Tagen im Angebot.", "{0} est en vente chez {1} depuis {2:N1} jours.", "{1}的{0}已上架 {2:N1} 天。", "{1}의 {0} 등록 후 {2:N1}일이 지났습니다.");
    public static string SalesVelocity => T("Velocity", "販売ペース", "Absatztempo", "Rythme des ventes", "销售速度", "판매 속도");
    public static string Velocity24h => T("24h", "24時間", "24 Std.", "24 h", "24小时", "24시간");
    public static string Velocity3d => T("3d", "3日", "3 Tage", "3 j", "3天", "3일");
    public static string Velocity7d => T("7d", "7日", "7 Tage", "7 j", "7天", "7일");
    public static string Velocity30d => T("30d", "30日", "30 Tage", "30 j", "30天", "30일");
    public static string VelocityCounts => T("{0:N0} units / {1:N0} sales", "{0:N0}個 / {1:N0}件", "{0:N0} Stk. / {1:N0} Verkäufe", "{0:N0} unités / {1:N0} ventes", "{0:N0} 件 / {1:N0} 笔", "{0:N0}개 / {1:N0}건");
    public static string VelocityHours7d => T("Avg. hours between sales (7d)", "平均販売間隔・時（7日）", "Ø Stunden zwischen Verkäufen (7 T.)", "Intervalle moyen en h (7 j)", "平均销售间隔（7天，小时）", "평균 판매 간격(7일, 시간)");
    public static string VelocityHours30d => T("Avg. hours between sales (30d)", "平均販売間隔・時（30日）", "Ø Stunden zwischen Verkäufen (30 T.)", "Intervalle moyen en h (30 j)", "平均销售间隔（30天，小时）", "평균 판매 간격(30일, 시간)");
    public static string VelocityHours7dShort => T("Avg. h (7d)", "平均時（7日）", "Ø Std. (7 T.)", "Moy. h (7 j)", "平均小时（7天）", "평균 시간(7일)");
    public static string VelocityHours30dShort => T("Avg. h (30d)", "平均時（30日）", "Ø Std. (30 T.)", "Moy. h (30 j)", "平均小时（30天）", "평균 시간(30일)");
    public static string VelocityTableLegend => T("Units / sales per period. Average hours between sales.", "期間ごとの販売個数 / 件数と平均販売間隔（時間）。", "Stück / Verkäufe pro Zeitraum. Durchschnittliche Stunden zwischen Verkäufen.", "Unités / ventes par période. Intervalle moyen entre les ventes en heures.", "各时段的销量 / 笔数，以及两笔销售之间的平均小时数。", "기간별 판매 수량 / 건수 및 평균 판매 간격(시간).");
    public static string VelocityStockDays => T("Days of stock", "在庫日数", "Reichweite in Tagen", "Jours de stock", "库存可售天数", "재고 일수");
    public static string SuggestedThreshold => T("Suggested threshold  {0:N0} (one day of sales at the 7-day rate)", "推奨しきい値  {0:N0}（直近7日の1日平均販売数）", "Vorgeschlagener Grenzwert  {0:N0} (ein Verkaufstag im 7-Tage-Schnitt)", "Seuil suggéré  {0:N0} (un jour de ventes au rythme des 7 derniers jours)", "建议阈值  {0:N0}（近7天的日均销量）", "추천 기준  {0:N0} (최근 7일 기준 하루 판매량)");

    public static string WindowTitle => "Restocky";

    // Tabs
    public static string TabReprice => T("Update prices", "価格更新", "Preise anpassen", "Mettre à jour les prix", "更新价格", "가격 갱신");
    public static string TabList    => T("List items", "新規出品", "Einstellen", "Mettre en vente", "上架", "등록");
    public static string TabSettings => T("Settings", "設定", "Einstellungen", "Paramètres", "设置", "설정");

    // Fork: purchase cost basis.
    public static string TabCosts => T("Costs", "原価", "Kosten", "Coûts", "成本", "원가");
    public static string ColProfit => T("Profit", "利益", "Gewinn", "Bénéfice", "利润", "이익");
    public static string CostAddManual => T("Add manual lot", "原価ロットを手動追加", "Charge manuell hinzufügen", "Ajouter un lot manuel", "手动添加批次", "수동으로 입고분 추가");
    public static string CostSearchItems => T("Search items", "アイテムを検索", "Artikel suchen", "Rechercher des objets", "搜索物品", "아이템 검색");
    public static string CostNoMatchingItems => T("No matching items.", "一致するアイテムがありません。", "Keine passenden Artikel.", "Aucun objet correspondant.", "没有匹配的物品。", "일치하는 아이템이 없습니다.");
    public static string CostNoLots => T("No matching cost lots.", "一致する原価ロットがありません。", "Keine passenden Chargen.", "Aucun lot correspondant.", "没有匹配的成本批次。", "일치하는 원가 입고분이 없습니다.");
    public static string CostHighQuality => T("HQ", "HQ", "HQ", "HQ", "HQ", "HQ");
    public static string CostDate => T("Date", "日時", "Datum", "Date", "日期", "날짜");
    public static string CostQuantity => T("Qty", "個数", "Anz.", "Qté", "数量", "수량");
    public static string CostRemaining => T("Qty left", "残り個数", "Restmenge", "Qté restante", "剩余数量", "남은 수량");
    public static string CostUnitCost => T("Unit cost", "単位原価", "Stückkosten", "Coût unitaire", "单位成本", "단위 원가");
    public static string CostSource => T("Source", "入手元", "Quelle", "Source", "来源", "출처");
    public static string CostDeleteLot => T("Delete", "削除", "Löschen", "Supprimer", "删除", "삭제");
    public static string CostWeightedAverage => T("Weighted average", "加重平均原価", "Gewichteter Mittelwert", "Moyenne pondérée", "加权平均成本", "가중 평균 원가");
    public static string CostPriceAfterTax => T("Price after {0:0.##%} tax  {1:N2}", "{0:0.##%}の税引き後価格  {1:N2}", "Preis nach {0:0.## %} Steuer  {1:N2}", "Prix après {0:0.## %} de taxe  {1:N2}", "扣除 {0:0.##%} 税费后的价格  {1:N2}", "{0:0.##%} 세금 공제 후 가격  {1:N2}");
    public static string CostFifoLots => T("FIFO lots", "原価ロット（先入先出）", "Chargen in FIFO-Reihenfolge", "Lots (premier entré, premier sorti)", "先进先出批次", "선입선출 입고분");
    public static string CostLotDetails => T("{0:yyyy-MM-dd HH:mm}  ·  {1:N0} left  ·  {2:N2} per unit  ·  {3}", "{0:yyyy-MM-dd HH:mm}  ·  残り{1:N0}個  ·  単位原価{2:N2}  ·  {3}", "{0:yyyy-MM-dd HH:mm}  ·  {1:N0} übrig  ·  {2:N2} pro Stück  ·  {3}", "{0:yyyy-MM-dd HH:mm}  ·  {1:N0} restants  ·  {2:N2} par unité  ·  {3}", "{0:yyyy-MM-dd HH:mm}  ·  剩余 {1:N0} 件  ·  单位成本 {2:N2}  ·  {3}", "{0:yyyy-MM-dd HH:mm}  ·  {1:N0}개 남음  ·  단위 원가 {2:N2}  ·  {3}");
    public static string CostSourceName(string source) => source == Market.CostLedger.MarketSource
        ? T("Market", "マーケット", "Markt", "Marché", "市场", "장터")
        : T("Manual", "手動", "Manuell", "Manuel", "手动", "수동");

    // Header
    public static string HeaderCachedRetainers => T("Cached:", "キャッシュ:", "Zwischenspeicher:", "Cache :", "缓存:", "캐시:");
    public static string HeaderRetainers => T("retainer(s)", "リテイナー", "Gehilfe(n)", "intendant(s)", "雇员", "리테이너");
    public static string HeaderLastUpdated => T("last update", "最終更新", "letzte Aktualisierung", "dernière mise à jour", "最后更新", "최종 갱신");
    public static string HeaderUnknown => T("never", "未取得", "nie", "jamais", "未获取", "미취득");
    public static string HeaderRefreshAll => T("Refresh all retainers", "全リテイナー更新", "Alle Gehilfen aktualisieren", "Actualiser tous les intendants", "刷新全部雇员", "전체 리테이너 새로고침");
    public static string HeaderRefreshing => T("Refreshing… {0}/{1}", "更新中… {0}/{1}", "Aktualisiere… {0}/{1}", "Actualisation… {0}/{1}", "刷新中… {0}/{1}", "새로고침 중… {0}/{1}");
    public static string HeaderStop => T("Stop", "中断", "Stopp", "Arrêter", "中断", "중단");
    public static string ToastBellNotOpen => T(
        "Open the retainer bell first.",
        "先にリテイナーベルを開いてください。",
        "Öffne zuerst die Gehilfenglocke.",
        "Ouvre d'abord la cloche des intendants.",
        "请先打开雇员铃。",
        "먼저 리테이너 벨을 여세요.");
    public static string ToastNoRetainersInBell => T(
        "No retainers detected in the bell list.",
        "ベルリストにリテイナーが見つかりません。",
        "Keine Gehilfen in der Glockenliste.",
        "Aucun intendant détecté.",
        "未在铃列表中检测到雇员。",
        "벨 목록에서 리테이너가 감지되지 않았습니다.");

    // Common
    public static string Apply => T("Apply", "適用", "Anwenden", "Appliquer", "应用", "적용");
    public static string Cancel => T("Cancel", "キャンセル", "Abbrechen", "Annuler", "取消", "취소");
    public static string NotImplemented => T("(not implemented)", "(未実装)", "(noch nicht)", "(à venir)", "（暂未）", "(미구현)");
    public static string Filter => T("Filter by item name", "アイテム名で絞り込み", "Nach Artikel filtern", "Filtrer par nom d'article", "按物品名筛选", "아이템 이름 필터");
    public static string HQNQNote => T("HQ/NQ are listed as separate rows", "HQ/NQ は別行扱い", "HQ/NQ als separate Zeilen", "HQ/NQ traités séparément", "HQ/NQ 视为独立行", "HQ/NQ는 별도 행");
    public static string NoTransfer => T("no items moved between retainers", "リテイナー間移動なし", "kein Transfer zwischen Gehilfen", "aucun transfert entre intendants", "雇员之间不转移", "리테이너 간 이동 없음");
    public static string EmptyHint => T(
        "Open the bell, summon a retainer and open the sell list — items will populate here.",
        "リテイナー召喚→マーケット出品リストを開けば、ここに集約表示されます。",
        "Glocke öffnen, Gehilfen rufen und die Verkaufsliste öffnen — die Artikel erscheinen hier.",
        "Ouvre la cloche, invoque un intendant et ouvre la liste de vente — les objets apparaîtront ici.",
        "打开雇员铃→召唤雇员→打开寄售列表，物品将出现在此处。",
        "벨을 열고 리테이너를 소환해 판매 목록을 열면 여기에 표시됩니다.");

    // Update prices tab
    public static string RepriceMatchLowest => T(
        "-1 gil to all rows",
        "全行に最安値 -1ギル",
        "-1 Gil auf alle Zeilen",
        "-1 gil à toutes les lignes",
        "全部行 -1 金币",
        "전체 행에 -1길");
    public static string RepriceMatchLowestTooltip => T(
        "Fetches market prices for each listing, then sets the new price. Press Apply afterwards to commit.",
        "各出品の相場を取得して価格をセットします。完了後に「適用」を押すと反映されます。",
        "Holt Marktpreise je Posten und setzt den neuen Preis. Danach Anwenden.",
        "Récupère les prix du marché par mise puis applique. Cliquez ensuite Appliquer.",
        "依次获取每个挂牌的市场价并设定新价格。完成后按「应用」确认。",
        "각 등록 항목의 시세를 가져와 새 가격을 설정합니다. 이후 적용을 눌러 반영합니다.");

    public static string RepriceFillRetainer => T(
        "fill",
        "他にコピー",
        "fill",
        "remplir",
        "填充",
        "복사");
    public static string RepriceFillTooltip => T(
        "Copy this price to every other listing on this retainer.",
        "この価格をこのリテイナーの他の全 listing にコピーします。",
        "Diesen Preis auf alle anderen Posten dieses Gehilfen anwenden.",
        "Copier ce prix sur toutes les autres mises de cet intendant.",
        "将此价格复制到该雇员的所有其他挂牌。",
        "이 가격을 이 리테이너의 다른 모든 등록에 복사.");

    public static string RepriceAddAllToPlan => T(
        "Add all to plan",
        "全てプランに追加",
        "Alle in Plan",
        "Tout au plan",
        "全部加入计划",
        "전부 플랜에");

    public static string RepriceMatchLowestExactThisRetainer => T(
        "Match lowest (exact) for all items",
        "全てのアイテムを最安値で価格更新",
        "Mindestpreis (exakt) (nur dieser Gehilfe)",
        "Prix minimum (exact, intendant uniquement)",
        "全部以最低价更新（同价）",
        "최저가 그대로 (이 리테이너)");

    public static string RepriceMatchLowestThisRetainer => T(
        "Match lowest minus this for all items",
        "全てのアイテムを最安値 -X ギルで価格更新",
        "-X Gil (nur dieser Gehilfe)",
        "-1 gil (cet intendant)",
        "本雇员 -1 金币",
        "이 리테이너에 -1길");
    public static string ApplyWithCount => T("{0} ({1})", "{0} ({1}件)", "{0} ({1})", "{0} ({1})", "{0} ({1})", "{0} ({1})");
    public static string MatchAppliedSummary => T(
        "{0} listings updated ({1} item type(s)).",
        "{0} 件の出品に適用しました（{1} 種類）。",
        "{0} Posten aktualisiert ({1} Artikelarten).",
        "{0} mises à jour ({1} type(s)).",
        "已更新 {0} 项（{1} 种）。",
        "{0}건 적용 ({1}종).");
    public static string MatchAppliedSummaryWithMissing => T(
        "{0} listings updated ({1} item type(s)). {2} type(s) had no market data — open the market for: {3}",
        "{0} 件の出品に適用（{1} 種類）。{2} 種類はマーケットデータ未取得 — 該当アイテムをマーケットで開いてください: {3}",
        "{0} aktualisiert ({1} Arten). {2} ohne Marktdaten: {3}",
        "{0} mises à jour ({1} types). {2} sans données de marché : {3}",
        "已更新 {0} 项（{1} 种）。{2} 种缺少市场数据：{3}",
        "{0}건 적용 ({1}종). {2}종 시장 데이터 없음: {3}");
    public static string ApplyProgress => T(
        "Applying… action {0}/{1}, retainer {2}/{3}",
        "適用中… アクション {0}/{1}、リテイナー {2}/{3}",
        "Anwenden… Aktion {0}/{1}, Gehilfe {2}/{3}",
        "Application… action {0}/{1}, intendant {2}/{3}",
        "应用中… 操作 {0}/{1}, 雇员 {2}/{3}",
        "적용 중… 작업 {0}/{1}, 리테이너 {2}/{3}");
    public static string RetainerHeader => T("{0}  ({1} listings, {2})", "{0}  ({1}件 / {2})", "{0}  ({1} Posten, {2})", "{0}  ({1} mises, {2})", "{0}  ({1} 项, {2})", "{0}  ({1}건, {2})");
    public static string RetainerHeaderInventory => T("{0}  ({1} items, {2})", "{0}  ({1}品 / {2})", "{0}  ({1} Artikel, {2})", "{0}  ({1} articles, {2})", "{0}  ({1} 物品, {2})", "{0}  ({1}품, {2})");
    public static string CharacterInventoryHeader => T("Character bag", "キャラ所持品", "Charakter-Tasche", "Sac du personnage", "角色背包", "캐릭터 가방");
    public static string SaddlebagHeader => T("Saddlebag", "チョコボかばん", "Satteltasche", "Fonte", "马鞍袋", "새들백");
    public static string CharBagTargetLabel => T("Target retainer", "出品先リテイナー", "Ziel-Gehilfe", "Intendant cible", "上架雇员", "등록 대상 리테이너");
    public static string CharBagPickTarget => T("(pick a target retainer)", "（出品先を選択）", "(Ziel wählen)", "(choisis un intendant)", "（请选择雇员）", "(대상 선택)");
    public static string CharBagNeedRetainerSnapshot => T(
        "No retainer snapshot yet — refresh retainers first to enable target selection.",
        "リテイナーの snapshot がまだありません。先に「全リテイナー更新」してください。",
        "Erst Gehilfen aktualisieren, dann Ziel wählen.",
        "Aucun snapshot — actualise d'abord les intendants.",
        "尚无雇员快照，请先刷新。",
        "리테이너 스냅샷이 없습니다. 먼저 새로고침하세요.");
    public static string FreeSlots => T("free slot(s)", "枠空き", "Slot(s)", "places", "空格", "빈 슬롯");
    public static string CollapseAll => T("Collapse all", "全て折りたたむ", "Alle einklappen", "Tout replier", "全部折叠", "모두 접기");
    public static string ExpandAll => T("Expand all", "全て展開", "Alle ausklappen", "Tout déplier", "全部展开", "모두 펼치기");
    public static string ColExpand => T("+/-", "展開", "+/-", "+/-", "展开", "확장");
    public static string ColItem => T("Item", "アイテム", "Artikel", "Article", "物品", "아이템");
    public static string ColTotalQty => T("Qty", "合計数", "Anz.", "Qté", "数量", "수량");
    public static string ColListings => T("Listings", "出品本数", "Posten", "Mises", "上架数", "출품 수");
    public static string ColCurrentPrice => T("Current", "現在価格", "Aktuell", "Actuel", "当前价格", "현재가");
    public static string ColNewPrice => T("New", "新価格", "Neu", "Nouveau", "新价格", "신가격");
    public static string ColHold => T("Hold", "保留", "Halten", "Bloquer", "锁定", "고정");
    public static string HoldTooltip => T(
        "Automatic cycles never change this item's price",
        "自動サイクルはこのアイテムの価格を変更しません",
        "Automatische Zyklen ändern den Preis dieses Artikels nie",
        "Les cycles automatiques ne changent jamais le prix de cet objet",
        "自动循环永不更改此物品的价格",
        "자동 주기는 이 아이템의 가격을 변경하지 않습니다");

    // Fork: undercutters tooltip in the Update prices table.
    public static string UndercuttersNoScan => T("No market scan yet", "マーケット情報は未取得", "Noch keine Marktabfrage", "Marché pas encore consulté", "尚未扫描市场", "아직 시장을 조회하지 않았습니다");
    public static string UndercuttersNone => T("Nobody cheaper in last scan", "前回取得時により安い出品はありませんでした", "Keine günstigeren Angebote bei der letzten Abfrage", "Aucune offre moins chère au dernier relevé", "上次扫描没有更便宜的挂单", "마지막 조회에서 더 저렴한 매물이 없었습니다");
    public static string UndercuttersHeader => T("Cheaper than you ({0:N0}): {1:N0} listings, {2:N0} units", "自分の価格（{0:N0}）より安い出品：{1:N0}件、計{2:N0}個", "Günstiger als dein Preis ({0:N0}): {1:N0} Angebote, {2:N0} Stück", "Moins cher que votre prix ({0:N0}) : {1:N0} offres, {2:N0} unités", "低于你的价格（{0:N0}）：{1:N0} 个挂单，共 {2:N0} 件", "내 가격({0:N0})보다 저렴한 매물: {1:N0}건, 총 {2:N0}개");
    public static string UndercuttersUnknownPrice => T("Your price unknown", "自分の価格は不明", "Dein Preis ist unbekannt", "Votre prix est inconnu", "你的价格未知", "내 가격을 알 수 없습니다");
    public static string UndercuttersSeller => T("Seller", "出品者", "Verkäufer", "Vendeur", "卖家", "판매자");
    public static string UndercuttersQuantity => T("Qty", "個数", "Anz.", "Qté", "数量", "수량");
    public static string UndercuttersUnitPrice => T("Unit price", "単価", "Stückpreis", "Prix unitaire", "单价", "개당 가격");
    public static string UndercuttersLast24h => T("24h", "24時間", "24 Std.", "24 h", "24小时", "24시간");
    public static string UndercuttersLast3d => T("3d", "3日", "3 T.", "3 j", "3天", "3일");
    public static string UndercuttersLast7d => T("1w", "1週間", "1 Wo.", "1 sem.", "1周", "1주");
    public static string UndercuttersMostActiveRivals => T("Most active rivals (1 week)", "自分より安い出品が多かった出品者（1週間）",
        "Aktivste Konkurrenten (1 Woche)", "Concurrents les plus actifs (1 semaine)", "最活跃的竞争对手（1周）", "가장 활발한 경쟁 판매자 (1주)");
    public static string UndercuttersCountHelp => T("Counts show scans where each seller was below your lowest known listing price.",
        "回数は、各出品者の価格が自分の確認済み最安値を下回っていた調査の回数です。",
        "Gezählt werden Abfragen, bei denen der Verkäufer deinen niedrigsten bekannten Angebotspreis unterboten hat.",
        "Nombre de relevés où le vendeur proposait un prix inférieur à votre prix de vente le plus bas connu.",
        "次数表示各卖家的价格低于你已知最低挂单价的扫描次数。", "각 판매자의 가격이 확인된 내 최저 판매가보다 낮았던 조회 횟수입니다.");
    public static string UndercuttersMore => T("+{0:N0} more", "ほか{0:N0}件", "+{0:N0} weitere", "+{0:N0} de plus", "另有 {0:N0} 个挂单", "외 {0:N0}건");
    public static string UndercuttersScannedNow => T("scanned just now", "たった今取得", "gerade abgefragt", "relevé à l’instant", "刚刚扫描", "방금 조회함");
    public static string UndercuttersScannedMinutes => T("scanned {0:N0} min ago", "{0:N0}分前に取得", "vor {0:N0} Min. abgefragt", "relevé il y a {0:N0} min", "{0:N0} 分钟前扫描", "{0:N0}분 전 조회함");

    public static string PriceUnknown => T("unknown", "未取得", "unbekannt", "inconnu", "未获取", "미취득");
    public static string EditedSummary => T("Editing: by item {0} / individual {1}", "編集中: アイテム単位 {0} / 個別 {1}", "Bearbeitung: pro Artikel {0} / einzeln {1}", "Édition : par article {0} / individuel {1}", "编辑中：按物品 {0} / 个别 {1}", "편집 중: 아이템 {0} / 개별 {1}");

    // List tab
    public static string ColOwned => T("Owned", "所持", "Bestand", "Stock", "拥有", "보유");
    public static string ColListed => T("Listed", "出品中", "Eingestellt", "En vente", "上架中", "출품 중");
    public static string ColMaxStack => T("Max/listing", "最大/出品", "Max./Posten", "Max/mise", "最大/单挂", "최대/등록");
    public static string ColPrice => T("Price", "提示価格", "Preis", "Prix", "价格", "가격");
    public static string ColPlan => T("Plan", "計画", "Plan", "Plan", "计划", "계획");
    public static string ColLotsConfig => T("Qty x Slots", "個数×枠数", "Anz x Plätze", "Qté x Lots", "数量×槽位", "수량×슬롯");
    public static string QtyMaxTooltip => T("Set qty to min(MaxStack, Owned)", "個数を min(最大スタック, 所持数) にする", "Anz auf min(MaxStack, Bestand) setzen", "Qté = min(MaxStack, Stock)", "设为 min(最大堆叠, 拥有数)", "수량을 min(최대스택, 보유)로 설정");
    public static string ColTarget => T("Target", "出品先", "Ziel", "Cible", "目标", "대상");
    public static string AddToPlan => T("+ Add", "+ 追加", "+ Hinzu", "+ Ajouter", "+ 添加", "+ 추가");
    public static string TargetSelf => T("(self)", "(自身)", "(selbst)", "(soi)", "(本人)", "(자신)");
    public static string PendingPlansHeader => T("Planned listings: {0}", "出品プラン: {0} 件", "Geplante Posten: {0}", "Mises planifiées : {0}", "已计划 {0} 项", "계획 {0}건");
    public static string PendingPlansEmpty => T("No plans yet. Set price/qty/slots/target on a row, then click +Add.", "プランが空です。各行で価格・個数・枠数・出品先を指定し +追加 を押してください。", "Noch keine Pläne. Pro Zeile Preis/Anz/Plätze/Ziel setzen, dann +Hinzu.", "Aucune mise. Renseignez prix/qté/lots/cible puis cliquez +Ajouter.", "暂无计划。在行内设定价格/数量/槽位/目标后点击 +添加。", "계획 없음. 행에 가격/수량/슬롯/대상을 설정한 뒤 +추가.");
    public static string PendingPlanRemove => T("x", "削除", "x", "x", "删除", "삭제");
    public static string ClearAllPlans => T("Clear all", "全消去", "Alle löschen", "Tout effacer", "全部清除", "모두 지움");

    // Progress dialog
    public static string ProgressMode => T("Mode", "モード", "Modus", "Mode", "模式", "모드");
    public static string ProgressModeRefresh => T("Refresh all", "全リテイナー更新", "Aktualisieren", "Rafraîchir", "全部刷新", "모두 갱신");
    public static string ProgressModeApply => T("Apply actions", "アクション実行", "Aktionen anwenden", "Exécuter", "执行操作", "작업 적용");
    public static string ProgressStep => T("Step", "ステップ", "Schritt", "Étape", "步骤", "단계");
    public static string ProgressJobs => T("Retainers: {0}/{1}", "リテイナー: {0}/{1}", "Gehilfen: {0}/{1}", "Intendants : {0}/{1}", "雇员: {0}/{1}", "리테이너: {0}/{1}");
    public static string ProgressActions => T("Actions: {0}/{1}", "操作: {0}/{1}", "Aktionen: {0}/{1}", "Actions : {0}/{1}", "操作: {0}/{1}", "작업: {0}/{1}");

    // Per-state labels
    public static string StateSelectingRetainer => T("Selecting retainer...", "リテイナーを選択中...", "Wähle Gehilfen...", "Sélection de l'intendant...", "选择雇员中...", "리테이너 선택 중...");
    public static string StateAwaitingSelectString => T("Waiting for retainer menu...", "リテイナーメニュー待ち...", "Warte auf Menü...", "Attente du menu...", "等待菜单...", "메뉴 대기...");
    public static string StateOpeningSellList => T("Opening sell list...", "出品リストを開く...", "Öffne Verkaufsliste...", "Ouverture liste de vente...", "打开上架列表...", "판매 목록 여는 중...");
    public static string StateAwaitingSellList => T("Waiting for sell list...", "出品リストを待機...", "Warte auf Verkaufsliste...", "Attente liste de vente...", "等待上架列表...", "판매 목록 대기...");
    public static string StatePerformingAction => T("Running listing actions...", "出品アクションを実行中...", "Führe Aktionen aus...", "Exécution des actions...", "执行上架操作...", "작업 실행 중...");
    public static string StateAwaitingContextMenu => T("Waiting for context menu...", "コンテキストメニュー待ち...", "Warte auf Kontextmenü...", "Attente menu contextuel...", "等待右键菜单...", "컨텍스트 메뉴 대기...");
    public static string StateClickingPutUpForSale => T("Selecting 'Put up for sale'...", "「マーケットに出品」を選択中...", "Wähle 'Anbieten'...", "Sélection 'Mettre en vente'...", "选择「市场出售」...", "「시장 등록」 선택 중...");
    public static string StateReadingPrices => T("Reading current prices...", "現在価格を読み取り中...", "Lese aktuelle Preise...", "Lecture des prix...", "读取当前价格...", "현재 가격 읽는 중...");
    public static string StateAwaitingSellDialog => T("Waiting for sell dialog...", "出品ダイアログを待機...", "Warte auf Dialog...", "Attente boîte de vente...", "等待价格对话框...", "판매 대화상자 대기...");
    public static string StateConfirmingSellDialog => T("Confirming listing...", "出品を確定中...", "Bestätige Posten...", "Confirmation...", "确认上架...", "등록 확정 중...");
    public static string StateAwaitingSaddleMove => T("Waiting for saddlebag→bag move...", "チョコボかばん→所持品への移動を待機中...", "Warte auf Sattel→Tasche...", "Attente sacoche→sac...", "等待马鞍→背包...", "안장→가방 이동 대기...");
    public static string StatePreStagingSaddle => T("Pre-staging saddlebag items to bag...", "チョコボかばんから所持品にアイテムを移動中...", "Bereite Sattel-Items vor...", "Préparation depuis sacoche...", "从马鞍预转入背包...", "안장→가방 사전 이동...");
    public static string StateAwaitingNewListing => T("Waiting for listing to land...", "出品反映を待機中...", "Warte auf Server-Bestätigung...", "Attente confirmation serveur...", "等待服务器确认...", "서버 반영 대기...");
    public static string StateFetchAwaitingMarketData => T("Reading market data...", "マーケット相場を取得中...", "Lese Marktdaten...", "Lecture du marché...", "读取市场数据...", "시장 데이터 조회 중...");
    public static string StateClosingSellList => T("Closing sell list...", "出品リストを閉じています...", "Schließe Liste...", "Fermeture...", "关闭上架列表...", "판매 목록 닫는 중...");
    public static string StateDismissingRetainer => T("Dismissing retainer...", "リテイナーを帰しています...", "Entlasse Gehilfen...", "Renvoi de l'intendant...", "送回雇员...", "리테이너 해산...");
    public static string StateAwaitingDismissed => T("Waiting for dismissal...", "退出待ち...", "Warte auf Entlassung...", "Attente fermeture...", "等待离开...", "퇴장 대기...");
    public static string StateDone => T("Done", "完了", "Fertig", "Terminé", "完成", "완료");
    public static string StateStopped => T("Stopped", "停止", "Gestoppt", "Arrêté", "已停止", "정지");
    public static string ListableOnly => T("Listable only", "出品可能アイテムのみ", "Nur einstellbare", "Vendables uniquement", "仅可上架", "등록 가능만");
    public static string Unsellable => T(" (not listable)", "（出品不可）", " (nicht einstellbar)", " (non vendable)", "（不可上架）", "（등록 불가）");
    public static string PlanCount => T("{0} listings", "{0}件", "{0} Posten", "{0} mises", "{0} 项", "{0}건");
    public static string PlanOverflow => T("{0} qty cannot fit", "※{0}個 余り", "{0} überzählig", "{0} en surplus", "余 {0} 个", "{0}개 초과");
    public static string ListSummary => T("To list: {0} item type(s)", "出品予定: {0} アイテム種", "Einzustellen: {0} Artikelart(en)", "À mettre en vente : {0} type(s)", "待上架：{0} 种物品", "등록 예정: {0} 종");
    public static string QuickLowest => T("low", "最安", "min", "min", "最低", "최저");
    public static string QuickLowestMinus1 => T("-1g", "-1g", "-1G", "-1g", "-1g", "-1g");
    public static string MarketDataNeeded => T(
        "No market data yet — open the marketboard for this item, or run -1g on a retainer that already lists it.",
        "マーケットデータ未取得 — 該当アイテムをマーケットボードで開くか、既存出品があるリテイナーで -1ギル を実行してください。",
        "Keine Marktdaten — Marktbrett für diesen Artikel öffnen.",
        "Pas de données — ouvre l'hôtel des ventes pour cet article.",
        "无市场数据 — 请在市场板查询该物品。",
        "시장 데이터 없음 — 시장 게시판에서 해당 아이템을 조회하세요.");

    // Confirm dialog
    public static string ConfirmTitle => T(
        "About to apply {0} actions across {1} retainer(s).",
        "{0} 件のアクションを {1} リテイナーに適用しようとしています。",
        "Wende {0} Aktionen auf {1} Gehilfen an.",
        "Sur le point d'appliquer {0} actions à {1} intendant(s).",
        "即将对 {1} 名雇员执行 {0} 个操作。",
        "{1}명의 리테이너에 {0}개 작업을 적용합니다.");
    public static string ConfirmBreakdown => T(
        "Reprice: {0} / New listings: {1}",
        "リプライス: {0} 件 / 新規出品: {1} 件",
        "Preis ändern: {0} / Neu einstellen: {1}",
        "Reprix : {0} / Nouvelles mises : {1}",
        "重新定价：{0} / 新上架：{1}",
        "재가격: {0} / 신규 등록: {1}");
    public static string ConfirmEta => T(
        "Estimated time: ~{0}s (each retainer ~5s overhead, ~3s per action)",
        "推定時間: 約 {0}秒（リテイナーごと約5秒、アクションごと約3秒）",
        "Geschätzt: ~{0}s",
        "Durée estimée : ~{0}s",
        "预计耗时：约 {0} 秒",
        "예상 시간: 약 {0}초");
    public static string ConfirmStart => T("Start", "開始", "Starten", "Démarrer", "开始", "시작");

    // Settings tab
    public static string SettingsAutoOpenOnBell => T(
        "Auto-show window when retainer bell is open",
        "リテイナーベル展開時にウィンドウを自動表示",
        "Fenster bei geöffneter Glocke automatisch anzeigen",
        "Afficher automatiquement quand la cloche est ouverte",
        "雇员铃打开时自动显示窗口",
        "벨이 열렸을 때 창 자동 표시");
    public static string SettingsLanguage => T("Language", "言語", "Sprache", "Langue", "语言", "언어");
    public static string SettingsLanguageAuto => T("Follow client", "クライアントに従う", "Spiel-Sprache folgen", "Suivre le client", "跟随客户端", "클라이언트 따름");

    // Toasts / warnings
    public static string ToastAutoRetainerPresent => T(
        "AutoRetainer is also loaded. Both plugins drive retainer summon flow — coordinate manually to avoid conflicts.",
        "AutoRetainer も読み込まれています。両者ともリテイナー巡回を制御するため、衝突を避けるよう手動で調整してください。",
        "AutoRetainer ist ebenfalls geladen. Beide Plugins steuern den Gehilfen-Aufruf — manuell koordinieren.",
        "AutoRetainer est aussi chargé. Les deux plugins pilotent l'invocation des intendants — coordonne manuellement.",
        "AutoRetainer 也已加载。两个插件都会驱动雇员召唤流程，请手动协调以避免冲突。",
        "AutoRetainer도 로드되어 있습니다. 두 플러그인 모두 리테이너 호출 흐름을 다루므로 충돌을 피하도록 수동 조정하세요.");
}
