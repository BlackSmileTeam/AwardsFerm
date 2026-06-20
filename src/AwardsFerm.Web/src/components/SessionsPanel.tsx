import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  addSlot,
  ApiError,
  checkApiHealth,
  createSessionHub,
  deleteSlot,
  ensureHubConnected,
  fetchAdAccounts,
  fetchProxies,
  fetchSessions,
  fetchSlots,
  isHubConnected,
  pauseSessionByProfile,
  resumeSessionByProfile,
  setPreviewByProfile,
  startSession,
  stopSessionByProfile,
  updateSlot,
} from '../api'
import {
  createEmptySlotState,
  type AdAccount,
  type ProxyConfig,
  type SessionEvent,
  type SessionSlotConfig,
  type SessionStatus,
  type SlotState,
} from '../types'
import { normalizeStatus, statusCssClass } from '../utils/session'
import { ServicesIndicator } from './ServicesIndicator'

const statusLabels: Record<SessionStatus, string> = {
  Idle: 'Ожидание',
  Starting: 'Запуск',
  Running: 'Выполняется',
  Completed: 'Завершено',
  Failed: 'Ошибка',
  Stopped: 'Остановлено',
  Paused: 'Пауза',
}

export function SessionsPanel() {
  const [accounts, setAccounts] = useState<AdAccount[]>([])
  const [selectedAccountId, setSelectedAccountId] = useState<number | null>(null)
  const [slotConfigs, setSlotConfigs] = useState<SessionSlotConfig[]>([])
  const [slots, setSlots] = useState<Record<string, SlotState>>({})
  const [notice, setNotice] = useState<{ kind: 'error' | 'success'; text: string } | null>(null)
  const [confirmState, setConfirmState] = useState<{
    title: string
    message: string
    confirmText: string
    cancelText: string
  } | null>(null)
  const [addingSlot, setAddingSlot] = useState(false)
  const [apiUp, setApiUp] = useState(false)
  const [signalRUp, setSignalRUp] = useState(false)
  const [proxies, setProxies] = useState<ProxyConfig[]>([])

  const sessionIdToProfile = useRef<Record<string, string>>({})
  const previewOpenRef = useRef<Record<string, boolean>>({})
  const confirmResolverRef = useRef<((value: boolean) => void) | null>(null)
  const slotConfigsRef = useRef(slotConfigs)
  const slotsRef = useRef(slots)
  const selectedAccountRef = useRef(selectedAccountId)

  slotConfigsRef.current = slotConfigs
  slotsRef.current = slots
  selectedAccountRef.current = selectedAccountId

  const selectedAccount = useMemo(
    () => accounts.find((a) => a.id === selectedAccountId) ?? null,
    [accounts, selectedAccountId],
  )

  const applyEventToSlot = useCallback((profileId: string, event: SessionEvent) => {
    setSlots((prev) => {
      const slot = prev[profileId] ?? createEmptySlotState()
      let next: SlotState = { ...slot }

      if (event.type === 'Log' && event.message) {
        next = {
          ...next,
          logs: [...next.logs, `[${formatTime(event.timestamp)}] ${event.message}`],
        }
      }

      if (event.type === 'StepChanged') {
        next = {
          ...next,
          session: next.session
            ? {
                ...next.session,
                status: 'Running',
                currentStep: event.currentStep ?? next.session.currentStep,
                totalSteps: event.totalSteps ?? next.session.totalSteps,
                currentStepName: event.stepName ?? next.session.currentStepName,
              }
            : next.session,
        }
        if (event.message) {
          next = { ...next, logs: [...next.logs, `[${formatTime(event.timestamp)}] ${event.message}`] }
        }
      }

      if (event.type === 'StatusChanged' && event.status) {
        next = {
          ...next,
          session: next.session ? { ...next.session, status: normalizeStatus(event.status) } : next.session,
        }
      }

      if (event.type === 'IpDetected' && event.publicIp) {
        next = {
          ...next,
          session: next.session ? { ...next.session, publicIp: event.publicIp } : next.session,
          logs: [
            ...next.logs,
            `[${formatTime(event.timestamp)}] ${event.message ?? `Текущий IP: ${event.publicIp}`}`,
          ],
        }
      }

      if (event.type === 'TrafficUpdated' && event.trafficBytes !== undefined) {
        next = {
          ...next,
          session: next.session
            ? { ...next.session, trafficBytes: event.trafficBytes }
            : next.session,
        }
      }

      if (event.type === 'Screenshot' && event.screenshotBase64 && previewOpenRef.current[profileId]) {
        next = { ...next, screenshotBase64: event.screenshotBase64 }
      }

      if (event.type === 'Completed') {
        next = {
          ...next,
          session: next.session
            ? {
                ...next.session,
                status: next.session.autoRestart ? 'Running' : 'Completed',
                finishedAt: next.session.autoRestart ? undefined : new Date().toISOString(),
              }
            : next.session,
          logs: [
            ...next.logs,
            `[${formatTime(event.timestamp)}] ${event.message ?? 'Сессия остановлена вручную'}`,
          ],
        }
      }

      if (event.type === 'Failed') {
        next = {
          ...next,
          session: next.session
            ? {
                ...next.session,
                status: next.session.autoRestart ? next.session.status : 'Failed',
                errorMessage: event.message,
                finishedAt: next.session.autoRestart ? undefined : new Date().toISOString(),
              }
            : next.session,
        }
        if (event.message) {
          next = { ...next, logs: [...next.logs, `[${formatTime(event.timestamp)}] ✗ ${event.message}`] }
        }
      }

      return { ...prev, [profileId]: next }
    })
  }, [])

  const handleEvent = useCallback(
    (event: SessionEvent) => {
      let profileId = sessionIdToProfile.current[event.sessionId]
      if (!profileId) {
        for (const slot of slotConfigsRef.current) {
          if (slotsRef.current[slot.profileId]?.session?.id === event.sessionId) {
            profileId = slot.profileId
            sessionIdToProfile.current[event.sessionId] = profileId
            break
          }
        }
      }
      if (profileId) applyEventToSlot(profileId, event)
    },
    [applyEventToSlot],
  )

  const loadAccounts = useCallback(async () => {
    const data = await fetchAdAccounts()
    setAccounts(data)
    if (
      data.length > 0 &&
      (selectedAccountRef.current === null || !data.some((a) => a.id === selectedAccountRef.current))
    ) {
      setSelectedAccountId(data[0].id)
    }
  }, [])

  const loadProxies = useCallback(async () => {
    try {
      setProxies(await fetchProxies())
    } catch {
      setProxies([])
    }
  }, [])

  const syncSlotsWithSessions = useCallback(async (adAccountId: number) => {
    const configs = await fetchSlots(adAccountId)
    setSlotConfigs(configs)
    setSlots((prev) => {
      const next: Record<string, SlotState> = {}
      for (const cfg of configs) {
        next[cfg.profileId] = prev[cfg.profileId] ?? createEmptySlotState()
      }
      return next
    })

    const sessions = await fetchSessions()
    setSlots((prev) => {
      const next = { ...prev }
      for (const cfg of configs) {
        const forProfile = sessions.filter(
          (s) => s.profileId === cfg.profileId && s.adAccountId === adAccountId,
        )
        const active = forProfile.find(
          (s) => s.status === 'Starting' || s.status === 'Running' || s.status === 'Paused',
        )
        const latest = forProfile.reduce<typeof sessions[number] | undefined>((best, s) => {
          if (!best) return s
          const bestTs = best.startedAt ? Date.parse(best.startedAt) : 0
          const sTs = s.startedAt ? Date.parse(s.startedAt) : 0
          return sTs >= bestTs ? s : best
        }, undefined)
        const display = active ?? latest
        if (display) {
          sessionIdToProfile.current[display.id] = cfg.profileId
          next[cfg.profileId] = {
            ...next[cfg.profileId],
            session: display,
            logs: display.logs.length > 0 ? display.logs : next[cfg.profileId]?.logs ?? [],
          }
        }
      }
      return next
    })
  }, [])

  useEffect(() => {
    createSessionHub(handleEvent)

    const sync = async () => {
      try {
        await loadAccounts()
        await loadProxies()
        const accountId = selectedAccountRef.current
        if (accountId) await syncSlotsWithSessions(accountId)
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) throw e
      }
      const apiOk = await checkApiHealth()
      setApiUp(apiOk)
      if (apiOk)
        await ensureHubConnected()
      setSignalRUp(isHubConnected())
    }

    void sync()
    const interval = window.setInterval(() => void sync(), 5000)
    return () => window.clearInterval(interval)
  }, [handleEvent, loadAccounts, loadProxies, syncSlotsWithSessions])

  useEffect(() => {
    if (selectedAccountId === null) return
    void syncSlotsWithSessions(selectedAccountId).catch(() => {})
  }, [selectedAccountId, syncSlotsWithSessions])

  const onPreviewChange = useCallback((profileId: string, open: boolean) => {
    previewOpenRef.current[profileId] = open
    void setPreviewByProfile(profileId, open).catch(() => {})
    if (!open) {
      setSlots((prev) => ({
        ...prev,
        [profileId]: { ...prev[profileId], screenshotBase64: null },
      }))
    }
  }, [])

  const onStart = async (profileId: string) => {
    if (!selectedAccountId || !selectedAccount) {
      setNotice({ kind: 'error', text: 'Выберите рекламный аккаунт' })
      return
    }

    const slot = slotConfigs.find((s) => s.profileId === profileId)
    setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: true } }))
    setNotice(null)

    try {
      const session = await startSession(selectedAccountId, profileId, {
        gameTitle: selectedAccount.gameTitle,
        gameUrl: selectedAccount.gameUrl,
        stopAtMsk: slot?.stopAtMsk,
        autoRestart: slot?.autoRestart ?? true,
        proxyEnabled: slot?.proxyEnabled ?? true,
      })
      sessionIdToProfile.current[session.id] = profileId
      setSlots((prev) => ({
        ...prev,
        [profileId]: {
          session,
          logs: [`[${formatTime(new Date().toISOString())}] Сессия ${session.id.slice(0, 8)}… запущена`],
          loading: false,
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка запуска' })
      setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: false } }))
    }
  }

  const onStop = async (profileId: string) => {
    setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: true } }))
    setNotice(null)
    try {
      await stopSessionByProfile(profileId)
      const session = slots[profileId]?.session
      if (session) delete sessionIdToProfile.current[session.id]
      if (selectedAccountId) await syncSlotsWithSessions(selectedAccountId)
      setSlots((prev) => ({
        ...prev,
        [profileId]: { session: null, logs: prev[profileId]?.logs ?? [], loading: false },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка остановки' })
      setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: false } }))
    }
  }

  const onPause = async (profileId: string) => {
    setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: true } }))
    try {
      await pauseSessionByProfile(profileId)
      setSlots((prev) => ({
        ...prev,
        [profileId]: {
          ...prev[profileId],
          session: prev[profileId].session
            ? { ...prev[profileId].session!, status: 'Paused' }
            : null,
          logs: [...(prev[profileId]?.logs ?? []), `[${formatTime(new Date().toISOString())}] Пауза`],
          loading: false,
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка паузы' })
      setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: false } }))
    }
  }

  const onResume = async (profileId: string) => {
    setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: true } }))
    try {
      await resumeSessionByProfile(profileId)
      setSlots((prev) => ({
        ...prev,
        [profileId]: {
          ...prev[profileId],
          session: prev[profileId].session
            ? { ...prev[profileId].session!, status: 'Running' }
            : null,
          logs: [...(prev[profileId]?.logs ?? []), `[${formatTime(new Date().toISOString())}] Продолжение`],
          loading: false,
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка продолжения' })
      setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: false } }))
    }
  }

  const onAddSlot = async () => {
    if (!selectedAccountId) return
    setAddingSlot(true)
    try {
      const created = await addSlot(selectedAccountId, `Сессия ${slotConfigs.length + 1}`)
      setSlotConfigs((prev) => [...prev, created])
      setSlots((prev) => ({ ...prev, [created.profileId]: createEmptySlotState() }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Не удалось добавить сессию' })
    } finally {
      setAddingSlot(false)
    }
  }

  const openConfirm = useCallback(
    (title: string, message: string, confirmText = 'Подтвердить', cancelText = 'Отмена') =>
      new Promise<boolean>((resolve) => {
        confirmResolverRef.current = resolve
        setConfirmState({ title, message, confirmText, cancelText })
      }),
    [],
  )

  const closeConfirm = useCallback((accepted: boolean) => {
    setConfirmState(null)
    const resolver = confirmResolverRef.current
    confirmResolverRef.current = null
    resolver?.(accepted)
  }, [])

  const onDeleteSlot = async (profileId: string) => {
    if (!selectedAccountId || slotConfigs.length <= 1) {
      setNotice({ kind: 'error', text: 'Нельзя удалить последнюю сессию' })
      return
    }

    const session = slots[profileId]?.session
    const isActive = session && (session.status === 'Starting' || session.status === 'Running' || session.status === 'Paused')
    const approved = await openConfirm(
      'Удаление сессии',
      isActive ? 'Сессия сейчас запущена. Остановить и удалить слот?' : 'Удалить этот слот?',
      'Удалить',
      'Отмена',
    )
    if (!approved) return

    try {
      if (isActive) {
        await stopSessionByProfile(profileId)
        const session = slots[profileId]?.session
        if (session) delete sessionIdToProfile.current[session.id]
      }
      await deleteSlot(selectedAccountId, profileId)
      setSlotConfigs((prev) => prev.filter((s) => s.profileId !== profileId))
      setSlots((prev) => {
        const next = { ...prev }
        delete next[profileId]
        return next
      })
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Не удалось удалить сессию' })
    }
  }

  const onSlotChange = async (
    profileId: string,
    patch: Partial<Pick<SessionSlotConfig, 'label' | 'scheduleEnabled' | 'scheduledStartMsk' | 'stopAtMsk' | 'autoRestart' | 'proxyEnabled' | 'proxyId'>>,
  ) => {
    if (!selectedAccountId) return
    try {
      const updated = await updateSlot(selectedAccountId, profileId, patch)
      setSlotConfigs((prev) => prev.map((s) => (s.profileId === profileId ? { ...s, ...updated } : s)))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Не удалось сохранить настройки' })
    }
  }

  const activeCount = useMemo(
    () =>
      slotConfigs.filter((s) => {
        const status = normalizeStatus(slots[s.profileId]?.session?.status)
        return status === 'Starting' || status === 'Running' || status === 'Paused'
      }).length,
    [slots, slotConfigs],
  )

  return (
    <>
      <div className="sessions-toolbar-meta">
        <span className="badge">
          Активно: {activeCount}/{slotConfigs.length}
        </span>
        <ServicesIndicator apiUp={apiUp} signalRUp={signalRUp} />
      </div>

      <div className="account-selector">
        <label>
          Рекламный аккаунт
          <select
            value={selectedAccountId ?? ''}
            onChange={(e) => setSelectedAccountId(Number(e.target.value))}
            disabled={accounts.length === 0}
          >
            {accounts.length === 0 ? (
              <option value="">Нет аккаунтов</option>
            ) : (
              accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name} — {a.gameTitle}
                </option>
              ))
            )}
          </select>
        </label>
        <button className="btn btn-secondary btn-sm" onClick={() => void onAddSlot()} disabled={addingSlot || !selectedAccountId || slotConfigs.length >= 10}>
          + Добавить сессию
        </button>
      </div>

      {notice && (
        <div className={`popup-notice ${notice.kind === 'error' ? 'popup-notice-error' : 'popup-notice-success'}`}>
          <span>{notice.text}</span>
          <button className="popup-notice-close" onClick={() => setNotice(null)}>
            ×
          </button>
        </div>
      )}

      {!selectedAccountId ? (
        <div className="error-banner">Создайте рекламный аккаунт во вкладке «Аккаунты».</div>
      ) : (
        <div className="sessions-grid">
          {slotConfigs.map((slot) => (
            <SessionCard
              key={slot.profileId}
              config={slot}
              proxies={proxies}
              state={slots[slot.profileId] ?? createEmptySlotState()}
              canDelete={slotConfigs.length > 1}
              onStart={() => void onStart(slot.profileId)}
              onStop={() => void onStop(slot.profileId)}
              onPause={() => void onPause(slot.profileId)}
              onResume={() => void onResume(slot.profileId)}
              onDelete={() => void onDeleteSlot(slot.profileId)}
              onSlotChange={(patch) => void onSlotChange(slot.profileId, patch)}
              onPreviewChange={(open) => onPreviewChange(slot.profileId, open)}
            />
          ))}
        </div>
      )}

      {confirmState && (
        <div className="popup-overlay">
          <div className="popup-dialog">
            <h3>{confirmState.title}</h3>
            <p>{confirmState.message}</p>
            <div className="popup-actions">
              <button className="btn btn-danger btn-sm" onClick={() => closeConfirm(true)}>
                {confirmState.confirmText}
              </button>
              <button className="btn btn-secondary btn-sm" onClick={() => closeConfirm(false)}>
                {confirmState.cancelText}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}

function SessionCard({
  config,
  proxies,
  state,
  canDelete,
  onStart,
  onStop,
  onPause,
  onResume,
  onDelete,
  onSlotChange,
  onPreviewChange,
}: {
  config: SessionSlotConfig
  proxies: ProxyConfig[]
  state: SlotState
  canDelete: boolean
  onStart: () => void
  onStop: () => void
  onPause: () => void
  onResume: () => void
  onDelete: () => void
  onSlotChange: (
    patch: Partial<Pick<SessionSlotConfig, 'scheduleEnabled' | 'scheduledStartMsk' | 'stopAtMsk' | 'autoRestart' | 'proxyEnabled' | 'proxyId'>>,
  ) => void
  onPreviewChange: (open: boolean) => void
}) {
  const logViewRef = useRef<HTMLDivElement>(null)
  const [copied, setCopied] = useState(false)
  const [previewOpen, setPreviewOpen] = useState(false)
  const sessionStatus = normalizeStatus(state.session?.status)
  const isRunning = sessionStatus === 'Starting' || sessionStatus === 'Running'
  const isPaused = sessionStatus === 'Paused'
  const isOccupied = isRunning || isPaused
  const progress =
    state.session && state.session.totalSteps > 0
      ? Math.round((state.session.currentStep / state.session.totalSteps) * 100)
      : 0
  const [, setTick] = useState(0)
  useEffect(() => {
    if (!isRunning) return
    const id = window.setInterval(() => setTick((t) => t + 1), 1000)
    return () => window.clearInterval(id)
  }, [isRunning])
  const durationText = formatDuration(state.session?.startedAt, state.session?.finishedAt, isRunning)
  const displayLogs = state.logs.length > 0 ? state.logs : state.session?.logs ?? []

  useEffect(() => {
    const el = logViewRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [displayLogs])

  useEffect(() => {
    if (!isOccupied && previewOpen) {
      setPreviewOpen(false)
      onPreviewChange(false)
    }
  }, [isOccupied, previewOpen, onPreviewChange])

  const togglePreview = () => {
    const next = !previewOpen
    setPreviewOpen(next)
    onPreviewChange(next)
  }

  const copyLogs = async () => {
    if (displayLogs.length === 0) return
    const text = displayLogs.join('\n')
    try {
      if (window.isSecureContext && navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text)
      } else {
        const textarea = document.createElement('textarea')
        textarea.value = text
        textarea.setAttribute('readonly', '')
        textarea.style.position = 'fixed'
        textarea.style.top = '-9999px'
        textarea.style.opacity = '0'
        document.body.appendChild(textarea)
        textarea.focus()
        textarea.select()
        const copied = document.execCommand('copy')
        document.body.removeChild(textarea)
        if (!copied) throw new Error('copy failed')
      }
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      /* ignore */
    }
  }

  return (
    <section className="session-panel">
      <div className="session-panel-header">
        <div>
          <h2>{config.label}</h2>
          <span className="profile-id">{config.profileId}</span>
          <div className="profile-ip">IP: {state.session?.publicIp ?? '—'}</div>
          <div className="profile-traffic">
            Трафик: {formatTrafficBytes(state.session?.trafficBytes ?? 0)}
          </div>
        </div>
        <div className="session-header-meta">
          <span className={`status-value status-${statusCssClass(state.session?.status)}`}>
            {statusLabels[sessionStatus]}
          </span>
          <span className="session-duration">{durationText}</span>
          {config.stopAtMsk && <span className="session-stop-at">до {config.stopAtMsk} МСК</span>}
        </div>
      </div>

      <div className="schedule-row">
        <label className="schedule-label">
          <input
            type="checkbox"
            checked={config.scheduleEnabled}
            onChange={(e) =>
              onSlotChange({
                scheduleEnabled: e.target.checked,
                scheduledStartMsk: config.scheduledStartMsk ?? '09:00',
              })
            }
          />
          Автозапуск (МСК)
        </label>
        <input
          type="time"
          className="schedule-time"
          value={config.scheduledStartMsk ?? '09:00'}
          disabled={!config.scheduleEnabled}
          onChange={(e) => onSlotChange({ scheduledStartMsk: e.target.value })}
        />
        <label className="schedule-label">Остановить (МСК)</label>
        <input
          type="time"
          className="schedule-time"
          value={config.stopAtMsk ?? ''}
          onChange={(e) => onSlotChange({ stopAtMsk: e.target.value || null })}
        />
      </div>

      <div className="schedule-row">
        <label className="schedule-label">
          <input
            type="checkbox"
            checked={config.proxyEnabled ?? true}
            onChange={(e) =>
              onSlotChange({
                proxyEnabled: e.target.checked,
                proxyId: e.target.checked ? config.proxyId ?? undefined : 0,
              })
            }
          />
          Прокси
        </label>
        {(config.proxyEnabled ?? true) && (
          <label className="schedule-label proxy-select-label">
            Прокси из списка
            <select
              className="proxy-select"
              value={config.proxyId ?? ''}
              onChange={(e) =>
                onSlotChange({
                  proxyId: e.target.value ? Number(e.target.value) : 0,
                })
              }
            >
              <option value="">— выберите —</option>
              {proxies.map((proxy) => (
                <option key={proxy.id} value={proxy.id}>
                  {proxy.name} ({proxy.host}:{proxy.port})
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="schedule-label">
          <input
            type="checkbox"
            checked={config.autoRestart ?? true}
            onChange={(e) => onSlotChange({ autoRestart: e.target.checked })}
          />
          Автоперезапуск
        </label>
      </div>

      <div className="session-toolbar">
        <button className="btn btn-primary btn-sm" onClick={onStart} disabled={state.loading || isOccupied}>
          ▶ Старт
        </button>
        {isPaused ? (
          <button className="btn btn-secondary btn-sm" onClick={onResume} disabled={state.loading}>
            ▶ Продолжить
          </button>
        ) : (
          <button
            className="btn btn-secondary btn-sm"
            onClick={onPause}
            disabled={state.loading || !isRunning}
          >
            ⏸ Пауза
          </button>
        )}
        <button className="btn btn-danger btn-sm" onClick={onStop} disabled={state.loading || !isOccupied}>
          ■ Стоп
        </button>
        <button
          className="btn btn-secondary btn-sm"
          onClick={onDelete}
          disabled={!canDelete || state.loading}
          title={canDelete ? 'Удалить слот' : 'Нельзя удалить последний слот'}
        >
          ✕
        </button>
        <button
          type="button"
          className={`btn btn-preview btn-sm${previewOpen ? ' btn-preview-active' : ''}`}
          onClick={togglePreview}
          disabled={!isOccupied}
          title={isOccupied ? 'Просмотр браузера' : 'Просмотр доступен во время сессии'}
        >
          <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
            <path
              fill="currentColor"
              d="M21 3H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h5v2h8v-2h5c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 14H3V5h18v12z"
            />
          </svg>
          Просмотр
        </button>
        <span className="step-hint">
          {state.session?.currentStep
            ? `Шаг ${state.session.currentStep}/${state.session.totalSteps}`
            : '—'}
        </span>
      </div>

      <div className="progress-bar progress-bar-sm">
        <div className="progress-fill" style={{ width: `${progress}%` }} />
      </div>

      {previewOpen && (
        <div className="session-preview">
          <div className="session-preview-header">
            <span>Экран браузера</span>
            <button type="button" className="btn btn-ghost btn-sm" onClick={togglePreview}>
              Скрыть
            </button>
          </div>
          <div className="browser-viewport">
            {state.screenshotBase64 ? (
              <img
                className="screenshot"
                src={`data:image/jpeg;base64,${state.screenshotBase64}`}
                alt="Экран браузера"
              />
            ) : (
              <span className="screenshot-placeholder">Ожидание кадра…</span>
            )}
          </div>
        </div>
      )}

      <div className="log-panel-header">
        <span className="log-panel-title">Лог</span>
        <button
          type="button"
          className={`btn-icon${copied ? ' btn-icon-ok' : ''}`}
          onClick={() => void copyLogs()}
          disabled={displayLogs.length === 0}
          title={copied ? 'Скопировано' : 'Копировать лог'}
          aria-label="Копировать лог"
        >
          {copied ? (
            <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
              <path
                fill="currentColor"
                d="M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2z"
              />
            </svg>
          ) : (
            <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
              <path
                fill="currentColor"
                d="M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z"
              />
            </svg>
          )}
        </button>
      </div>

      <div ref={logViewRef} className="log-view session-log session-log-tall">
        {displayLogs.length === 0 ? (
          <span className="log-empty">Лог пуст — нажмите «Старт»</span>
        ) : (
          displayLogs.map((line, i) => (
            <div
              key={i}
              className={`log-line${line.includes('⚠') || /ошибк|прокси|failed/i.test(line) ? ' log-line-warn' : ''}`}
            >
              {line}
            </div>
          ))
        )}
      </div>
    </section>
  )
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString('ru-RU')
  } catch {
    return '--:--:--'
  }
}

function formatTrafficBytes(bytes: number): string {
  if (bytes <= 0) return '0 Б'
  if (bytes < 1024) return `${bytes} Б`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} КБ`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} МБ`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} ГБ`
}

function formatDuration(startedAt?: string, finishedAt?: string, isLive = false): string {
  if (!startedAt) return '00:00:00'
  const startTs = new Date(startedAt).getTime()
  if (Number.isNaN(startTs)) return '00:00:00'
  const endTs = isLive
    ? Date.now()
    : finishedAt
      ? new Date(finishedAt).getTime()
      : startTs
  const safeEnd = Number.isNaN(endTs) ? startTs : endTs
  const totalSeconds = Math.max(0, Math.floor((safeEnd - startTs) / 1000))
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds
    .toString()
    .padStart(2, '0')}`
}
