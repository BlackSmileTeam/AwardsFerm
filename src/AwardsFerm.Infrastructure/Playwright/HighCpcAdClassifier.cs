namespace AwardsFerm.Infrastructure.Playwright;

using Microsoft.Playwright;

/// <summary>
/// Классификация рекламы по вертикалям с высоким CPC (Яндекс Директ / РСЯ).
/// Приоритет: юридические → медицина → недвижимость → финансы → B2B-оборудование → прочее.
/// </summary>
internal static class HighCpcAdClassifier
{
    internal enum HighCpcCategory
    {
        Unknown = 0,
        IndustrialB2B = 1,
        Finance = 2,
        RealEstate = 3,
        Medical = 4,
        Legal = 5
    }

    private sealed record CategoryRule(HighCpcCategory Category, string Label, int MinCpcRub, int MaxCpcRub, string[] Keywords);

    private static readonly CategoryRule[] Rules =
    [
        new(HighCpcCategory.Legal, "Юридические услуги", 500, 1000,
        [
            "юрист", "адвокат", "юридическ", "правов", "банкротств", "дтп", "автоюрист",
            "наследств", "развод", "алимент", "суд", "иск", "нотариус", "арбитраж",
            "уголовн", "гражданск", "консультац", "защит", "представительств"
        ]),
        new(HighCpcCategory.Medical, "Медицинские услуги", 500, 700,
        [
            "стоматолог", "имплант", "протезирован", "пластическ", "хирург", "клиник",
            "мрт", "кт ", "узи", "диагност", "лечен", "медицин", "стоматолог", "ортодонт",
            "косметолог", "лазер", "операци", "гинеколог", "уролог", "офтальмолог",
            "реабилитац", "стоматология", "зуб", "винир"
        ]),
        new(HighCpcCategory.RealEstate, "Недвижимость и строительство", 300, 600,
        [
            "недвижим", "квартир", "новострой", "жк ", "ипотек", "застройщик", "дом ",
            "коттедж", "таунхаус", "аренд", "риэлтор", "риелтор", "строительств",
            "ремонт квартир", "отделк", "дизайн интерьер", "участок", "земел", "коммерческ",
            "склад", "офис", "пентхаус", "апартамент"
        ]),
        new(HighCpcCategory.Finance, "Финансы (кредиты, займы)", 300, 800,
        [
            "кредит", "займ", "микрозайм", "мфо", "банк", "дебетов", "кредитн",
            "рефинансир", "ипотек", "вклад", "депозит", "кредитная карта", "рассрочк",
            "инвестиц", "брокер", "форекс", "страхован", "осаго", "каско", "лизинг",
            "финанс", "процент", "одобрен"
        ]),
        new(HighCpcCategory.IndustrialB2B, "Промышленное оборудование (B2B)", 200, 500,
        [
            "оборудован", "станок", "станки", "компрессор", "насос", "генератор",
            "промышленн", "b2b", "оптом", "поставк", "складск", "конвейер", "токарн",
            "фрезерн", "сварочн", "кран ", "погрузчик", "экскаватор", "спецтехник",
            "металлообработ", "производств", "завод", "фабрик"
        ])
    ];

    internal static HighCpcCategory Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return HighCpcCategory.Unknown;

        var normalized = text.ToLowerInvariant();
        HighCpcCategory best = HighCpcCategory.Unknown;
        var bestScore = 0;

        foreach (var rule in Rules)
        {
            var score = 0;
            foreach (var keyword in rule.Keywords)
            {
                if (normalized.Contains(keyword, StringComparison.Ordinal))
                    score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = rule.Category;
            }
        }

        return best;
    }

    internal static string Describe(HighCpcCategory category)
    {
        if (category == HighCpcCategory.Unknown)
            return "Прочая реклама (любая доступная)";

        var rule = Rules.First(r => r.Category == category);
        return $"{rule.Label} (~{rule.MinCpcRub}–{rule.MaxCpcRub} ₽ CPC)";
    }

    internal static int Priority(HighCpcCategory category) => (int)category;

    internal static async Task<HighCpcCategory> ClassifyPageAsync(IPage page)
    {
        if (page.IsClosed)
            return HighCpcCategory.Unknown;

        try
        {
            var blob = await page.EvaluateAsync<string?>(
                """
                () => {
                  const title = document.title || '';
                  const meta = document.querySelector('meta[name="description"]')?.content || '';
                  const h1 = [...document.querySelectorAll('h1')].slice(0, 3).map(e => e.innerText).join(' ');
                  const body = (document.body?.innerText || '').slice(0, 4000);
                  return [location.href, title, meta, h1, body].join('\n');
                }
                """);

            return Classify(blob);
        }
        catch
        {
            try
            {
                return Classify($"{page.Url}\n{await page.TitleAsync()}");
            }
            catch
            {
                return HighCpcCategory.Unknown;
            }
        }
    }

    internal static async Task<string?> ProbeStickyAdTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                () => {
                  const selectors = [
                    '#yandex-adv-sticky-banner-desktop',
                    '.yandex-sticky-adv-banner__desktop-wrapper',
                    '.yandex-sticky-adv-banner_desktop_right:not(.yandex-sticky-adv-banner_hidden)'
                  ];
                  const parts = [];
                  for (const sel of selectors) {
                    const el = document.querySelector(sel);
                    if (!el) continue;
                    parts.push(el.innerText || '');
                    for (const img of el.querySelectorAll('img')) {
                      if (img.alt) parts.push(img.alt);
                      if (img.title) parts.push(img.title);
                    }
                    for (const iframe of el.querySelectorAll('iframe')) {
                      if (iframe.src) parts.push(iframe.src);
                      if (iframe.title) parts.push(iframe.title);
                    }
                    for (const a of el.querySelectorAll('a[href]')) {
                      parts.push(a.href);
                      parts.push(a.innerText || '');
                    }
                  }
                  const text = parts.join(' ').trim();
                  return text.length ? text : null;
                }
                """);
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string?> PickBestLinkHrefAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                (rules) => {
                  const current = location.href.split('#')[0];
                  const links = [...document.querySelectorAll('a[href]')]
                    .filter(a => {
                      const h = a.href;
                      if (!h || h.startsWith('javascript:') || h.startsWith('mailto:')) return false;
                      if (h.split('#')[0] === current) return false;
                      const r = a.getBoundingClientRect();
                      return r.width > 30 && r.height > 12 && r.top >= 0 && r.top < innerHeight;
                    })
                    .slice(0, 40)
                    .map(a => ({
                      href: a.href,
                      text: [a.innerText, a.title, a.getAttribute('aria-label'), a.href].filter(Boolean).join(' ')
                    }));

                  if (!links.length) return null;

                  const scoreLink = (text) => {
                    const n = (text || '').toLowerCase();
                    let best = 0;
                    for (const rule of rules) {
                      let s = 0;
                      for (const kw of rule.keywords) {
                        if (n.includes(kw)) s++;
                      }
                      if (s > best) best = s;
                    }
                    return best;
                  };

                  let bestHref = null;
                  let bestScore = -1;
                  for (const link of links) {
                    const s = scoreLink(link.text);
                    if (s > bestScore) {
                      bestScore = s;
                      bestHref = link.href;
                    }
                  }

                  if (bestScore > 0) return bestHref;

                  const pick = links[Math.floor(Math.random() * Math.min(links.length, 12))];
                  return pick.href;
                }
                """,
                Rules.Select(r => new { category = r.Category.ToString(), keywords = r.Keywords }).ToArray());
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string?> PickBestFullscreenAdSelectorAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(
                """
                (rules) => {
                  const selectors = [
                    '[data-testid="yandex-fullscreen-render-button"]',
                    '.play-modal_visible a',
                    '.play-yandex-modal_visible a',
                    '.play-modal_visible',
                    '.play-yandex-modal_visible'
                  ];
                  const candidates = [];
                  for (const sel of selectors) {
                    const nodes = document.querySelectorAll(sel);
                    for (const el of nodes) {
                      if (!el || el.offsetParent === null) continue;
                      const r = el.getBoundingClientRect();
                      if (r.width < 40 || r.height < 40) continue;
                      const text = [
                        el.innerText, el.title, el.getAttribute('aria-label'),
                        el.tagName === 'A' ? el.href : '',
                        el.querySelector('img')?.alt || ''
                      ].filter(Boolean).join(' ');
                      candidates.push({ selector: sel, text });
                    }
                  }
                  if (!candidates.length) return null;

                  const score = (text) => {
                    const n = (text || '').toLowerCase();
                    let best = 0;
                    for (const rule of rules) {
                      let s = 0;
                      for (const kw of rule.keywords) {
                        if (n.includes(kw)) s++;
                      }
                      if (s > best) best = s;
                    }
                    return best;
                  };

                  let best = candidates[0];
                  let bestScore = score(best.text);
                  for (let i = 1; i < candidates.length; i++) {
                    const s = score(candidates[i].text);
                    if (s > bestScore) {
                      bestScore = s;
                      best = candidates[i];
                    }
                  }
                  return best.selector;
                }
                """,
                Rules.Select(r => new { category = r.Category.ToString(), keywords = r.Keywords }).ToArray());
        }
        catch
        {
            return null;
        }
    }
}
