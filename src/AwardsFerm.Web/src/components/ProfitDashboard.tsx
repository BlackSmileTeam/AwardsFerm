import { useEffect, useState } from 'react'
import { fetchUserProfit } from '../api'
import type { UserProfitSummary } from '../types'

const REFRESH_MS = 120_000

export function ProfitDashboard() {
  const [data, setData] = useState<UserProfitSummary | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    try {
      const summary = await fetchUserProfit()
      setData(summary)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка загрузки прибыли')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    let cancelled = false
    const run = async () => {
      await load()
      if (cancelled) return
    }
    void run()
    const interval = window.setInterval(() => void load(), REFRESH_MS)
    return () => {
      cancelled = true
      window.clearInterval(interval)
    }
  }, [])

  if (loading && !data) {
    return (
      <section className="rsya-panel">
        <div className="rsya-panel-header">
          <h2>Прибыль по РСЯ</h2>
          <span className="rsya-muted">Загрузка…</span>
        </div>
      </section>
    )
  }

  return (
    <section className="rsya-panel">
      <div className="rsya-panel-header">
        <div>
          <h2>Прибыль по РСЯ</h2>
          <p className="rsya-subtitle">Сумма по всем рекламным аккаунтам</p>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={() => void load()}>
          ↻
        </button>
      </div>

      {error && <div className="rsya-error">{error}</div>}

      <div className="rsya-cards rsya-cards-profit">
        <StatCard label="Сегодня" value={formatMoney(data?.totalTodayReward ?? 0)} accent />
        <StatCard label="Месяц" value={formatMoney(data?.totalMonthReward ?? 0)} />
      </div>

      {(data?.accounts.length ?? 0) > 0 && (
        <div className="profit-table-wrap">
          <table className="profit-table">
            <thead>
              <tr>
                <th>Аккаунт</th>
                <th>Игра</th>
                <th>Сегодня</th>
                <th>Месяц</th>
              </tr>
            </thead>
            <tbody>
              {data!.accounts.map((account) => (
                <tr key={account.id}>
                  <td>{account.name}</td>
                  <td className="profit-game">{account.gameTitle}</td>
                  <td>{formatMoney(account.todayReward ?? 0)}</td>
                  <td>{formatMoney(account.monthReward ?? 0)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function StatCard({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  return (
    <div className={`rsya-card${accent ? ' rsya-card-accent' : ''}`}>
      <span className="rsya-card-label">{label}</span>
      <span className="rsya-card-value">{value}</span>
    </div>
  )
}

function formatMoney(value: number): string {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    maximumFractionDigits: 2,
  }).format(value)
}
