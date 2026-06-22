import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
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
  previewClickByProfile,
  previewCloseCaptchaTabByProfile,
  previewReloadByProfile,
  fetchBrowserTabsByProfile,
  closeBrowserTabByProfile,
  resumeSessionByProfile,
  setPreviewByProfile,
  fetchPreviewFrame,
  startSession,
  stopSessionByProfile,
  updateSlot,
} from '../api'
import {
  createEmptySlotState,
  DEVICE_PLATFORM_OPTIONS,
  type AdAccount,
  type BrowserTabInfo,
  type ProxyConfig,
  type SessionDevicePlatform,
  type SessionEvent,
  type SessionSlotConfig,
  type SessionStatus,
  type SlotState,
} from '../types'
import { isCaptchaPending, hasActiveSessionError, isSessionErrorResolvedLog, normalizeStatus, statusCssClass, usePinnedScroll } from '../utils/session'
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
  const [errorHighlightOn, setErrorHighlightOn] = useState(false)

  const sessionCardRefs = useRef<Record<string, HTMLElement | null>>({})

  const sessionIdToProfile = useRef<Record<string, string>>({})
  const stopPromisesRef = useRef<Record<string, Promise<void>>>({})
  const locallyStoppedUntilRef = useRef<Record<string, number>>({})
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
        const line = `[${formatTime(event.timestamp)}] ${event.message}`
        const clearsError = isSessionErrorResolvedLog(line)
        next = {
          ...next,
          logs: [...next.logs, line],
          session:
            next.session && clearsError
              ? { ...next.session, errorMessage: undefined }
              : next.session,
        }
      }

      if (event.type === 'DiagnosticLog' && event.message) {
        next = {
          ...next,
          diagnosticLogs: [
            ...next.diagnosticLogs,
            `[${formatTime(event.timestamp)}]\n${event.message}`,
          ],
          logs: [
            ...next.logs,
            `[${formatTime(event.timestamp)}] ⚠ Диагностика: см. отдельный лог`,
          ],
        }
      }

      if (event.type === 'StepChanged') {
        next = {
          ...next,
          session: next.session
            ? {
                ...next.session,
                status: 'Running',
                errorMessage: undefined,
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
        const status = normalizeStatus(event.status)
        const terminal = status === 'Stopped' || status === 'Completed' || status === 'Failed'
        const locallyStopped = (locallyStoppedUntilRef.current[profileId] ?? 0) > Date.now()
        next = {
          ...next,
          loading: terminal ? false : next.loading,
          session:
            terminal || locallyStopped
              ? null
              : next.session
                ? { ...next.session, status }
                : next.session,
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

      if (event.type === 'Completed') {
        const keepRunning = Boolean(next.session?.autoRestart)
        next = {
          ...next,
          loading: false,
          session: next.session
            ? keepRunning
              ? { ...next.session, status: 'Running' as const }
              : {
                  ...next.session,
                  status: 'Completed' as const,
                  finishedAt: new Date().toISOString(),
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
                errorMessage: next.session.autoRestart ? undefined : event.message,
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
        const locallyStopped = (locallyStoppedUntilRef.current[cfg.profileId] ?? 0) > Date.now()
        if (active && !locallyStopped) {
          sessionIdToProfile.current[active.id] = cfg.profileId
          next[cfg.profileId] = {
            ...next[cfg.profileId],
            session: active,
            loading: false,
            logs: active.logs.length > 0 ? active.logs : next[cfg.profileId]?.logs ?? [],
            diagnosticLogs:
              active.diagnosticLogs?.length > 0
                ? active.diagnosticLogs
                : next[cfg.profileId]?.diagnosticLogs ?? [],
          }
        } else {
          next[cfg.profileId] = {
            ...next[cfg.profileId],
            session: null,
            loading: false,
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

  const onScreenshotUpdate = useCallback((profileId: string, base64: string | null) => {
    setSlots((prev) => ({
      ...prev,
      [profileId]: { ...prev[profileId], screenshotBase64: base64 },
    }))
  }, [])

  const onPreviewClose = useCallback((profileId: string) => {
    void setPreviewByProfile(profileId, false).catch(() => {})
    setSlots((prev) => ({
      ...prev,
      [profileId]: { ...prev[profileId], screenshotBase64: null },
    }))
  }, [])

  const onStart = async (profileId: string) => {
    if (!selectedAccountId || !selectedAccount) {
      setNotice({ kind: 'error', text: 'Выберите рекламный аккаунт' })
      return
    }

    const pendingStop = stopPromisesRef.current[profileId]
    if (pendingStop) {
      try {
        await pendingStop
      } catch {
        /* stop failed — still try start; API will ensure clean state */
      }
    }

    const slot = slotConfigs.find((s) => s.profileId === profileId)
    delete locallyStoppedUntilRef.current[profileId]
    setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: true } }))
    setNotice(null)

    try {
      const session = await startSession(selectedAccountId, profileId, {
        gameTitle: selectedAccount.gameTitle,
        gameUrl: selectedAccount.gameUrl,
        stopAtMsk: slot?.stopAtMsk,
        autoRestart: slot?.autoRestart ?? true,
        proxyEnabled: slot?.proxyEnabled ?? true,
        devicePlatform: slot?.devicePlatform ?? 'Random',
      })
      sessionIdToProfile.current[session.id] = profileId
      setSlots((prev) => ({
        ...prev,
        [profileId]: {
          session,
          logs: [`[${formatTime(new Date().toISOString())}] Сессия ${session.id.slice(0, 8)}… запущена`],
          diagnosticLogs: [],
          loading: false,
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка запуска' })
    } finally {
      setSlots((prev) => ({ ...prev, [profileId]: { ...prev[profileId], loading: false } }))
    }
  }

  const onStop = async (profileId: string) => {
    onPreviewClose(profileId)
    setNotice(null)
    locallyStoppedUntilRef.current[profileId] = Date.now() + 30_000

    setSlots((prev) => {
      const slot = prev[profileId]
      const session = slot?.session
      if (session) delete sessionIdToProfile.current[session.id]
      return {
        ...prev,
        [profileId]: {
          ...slot,
          session: null,
          loading: false,
          screenshotBase64: null,
          logs: [
            ...(slot?.logs ?? []),
            `[${formatTime(new Date().toISOString())}] Остановка сессии…`,
          ],
          diagnosticLogs: slot?.diagnosticLogs ?? [],
        },
      }
    })

    const stopTask = stopSessionByProfile(profileId)
      .catch((e: unknown) => {
        delete locallyStoppedUntilRef.current[profileId]
        setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка остановки' })
        if (selectedAccountRef.current) void syncSlotsWithSessions(selectedAccountRef.current)
        throw e
      })
      .finally(() => {
        delete stopPromisesRef.current[profileId]
      })

    stopPromisesRef.current[profileId] = stopTask
    void stopTask
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
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка паузы' })
    } finally {
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
        },
      }))
    } catch (e) {
      setNotice({ kind: 'error', text: e instanceof Error ? e.message : 'Ошибка продолжения' })
    } finally {
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

  const onClearLogs = useCallback((profileId: string) => {
    setSlots((prev) => {
      const slot = prev[profileId]
      if (!slot) return prev
      return {
        ...prev,
        [profileId]: {
          ...slot,
          logs: [],
          diagnosticLogs: [],
          session: slot.session
            ? { ...slot.session, logs: [], diagnosticLogs: [] }
            : null,
        },
      }
    })
  }, [])

  const onSlotChange = async (
    profileId: string,
    patch: Partial<Pick<SessionSlotConfig, 'label' | 'scheduleEnabled' | 'scheduledStartMsk' | 'stopAtMsk' | 'autoRestart' | 'proxyEnabled' | 'proxyId' | 'devicePlatform'>>,
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

  const errorProfileIds = useMemo(
    () =>
      slotConfigs
        .filter((cfg) => {
          const st = slots[cfg.profileId]
          const logs = st?.logs.length ? st.logs : st?.session?.logs ?? []
          return hasActiveSessionError(st?.session?.status, logs, st?.session?.errorMessage)
        })
        .map((cfg) => cfg.profileId),
    [slotConfigs, slots],
  )

  const hasActiveSessionErrors = errorProfileIds.length > 0

  useEffect(() => {
    if (!hasActiveSessionErrors) setErrorHighlightOn(false)
  }, [hasActiveSessionErrors])

  const toggleErrorHighlight = () => {
    if (!hasActiveSessionErrors) return
    setErrorHighlightOn((prev) => {
      const next = !prev
      if (next) {
        window.requestAnimationFrame(() => {
          sessionCardRefs.current[errorProfileIds[0]]?.scrollIntoView({
            behavior: 'smooth',
            block: 'nearest',
          })
        })
      }
      return next
    })
  }

  return (
    <>
      <div className="sessions-toolbar-meta">
        {hasActiveSessionErrors ? (
          <button
            type="button"
            className={`badge badge-error badge-clickable${errorHighlightOn ? ' badge-error-active' : ''}`}
            onClick={toggleErrorHighlight}
            title={
              errorHighlightOn
                ? 'Скрыть подсветку сессий с ошибками'
                : 'Показать сессии с ошибками'
            }
          >
            Активно: {activeCount}/{slotConfigs.length}
          </button>
        ) : (
          <span className="badge">
            Активно: {activeCount}/{slotConfigs.length}
          </span>
        )}
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
              highlightError={errorHighlightOn && errorProfileIds.includes(slot.profileId)}
              panelRef={(el) => {
                sessionCardRefs.current[slot.profileId] = el
              }}
              onStart={() => void onStart(slot.profileId)}
              onStop={() => void onStop(slot.profileId)}
              onPause={() => void onPause(slot.profileId)}
              onResume={() => void onResume(slot.profileId)}
              onDelete={() => void onDeleteSlot(slot.profileId)}
              onSlotChange={(patch) => void onSlotChange(slot.profileId, patch)}
              onPreviewClose={() => onPreviewClose(slot.profileId)}
              onScreenshotUpdate={(base64) => onScreenshotUpdate(slot.profileId, base64)}
              onClearLogs={() => onClearLogs(slot.profileId)}
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
  highlightError = false,
  panelRef,
  onStart,
  onStop,
  onPause,
  onResume,
  onDelete,
  onSlotChange,
  onPreviewClose,
  onScreenshotUpdate,
  onClearLogs,
}: {
  config: SessionSlotConfig
  proxies: ProxyConfig[]
  state: SlotState
  canDelete: boolean
  highlightError?: boolean
  panelRef?: (el: HTMLElement | null) => void
  onStart: () => void
  onStop: () => void
  onPause: () => void
  onResume: () => void
  onDelete: () => void
  onSlotChange: (
    patch: Partial<Pick<SessionSlotConfig, 'scheduleEnabled' | 'scheduledStartMsk' | 'stopAtMsk' | 'autoRestart' | 'proxyEnabled' | 'proxyId' | 'devicePlatform'>>,
  ) => void
  onPreviewClose: () => void
  onScreenshotUpdate: (base64: string | null) => void
  onClearLogs: () => void
}) {
  const logViewRef = useRef<HTMLDivElement>(null)
  const diagnosticViewRef = useRef<HTMLDivElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const screenshotRef = useRef<HTMLImageElement>(null)
  const [copied, setCopied] = useState(false)
  const [diagnosticCopied, setDiagnosticCopied] = useState(false)
  const [diagnosticExpanded, setDiagnosticExpanded] = useState(true)
  const [menuOpen, setMenuOpen] = useState(false)
  const [clickPending, setClickPending] = useState(false)
  const [clickError, setClickError] = useState<string | null>(null)
  const [previewActionPending, setPreviewActionPending] = useState(false)
  const [previewActionError, setPreviewActionError] = useState<string | null>(null)
  const [tabsOpen, setTabsOpen] = useState(false)
  const [tabsLoading, setTabsLoading] = useState(false)
  const [tabsError, setTabsError] = useState<string | null>(null)
  const [browserTabs, setBrowserTabs] = useState<BrowserTabInfo[]>([])
  const [tabClosePending, setTabClosePending] = useState<number | null>(null)
  const [previewOpen, setPreviewOpen] = useState(false)
  const [previewFullscreen, setPreviewFullscreen] = useState(false)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [previewWaiting, setPreviewWaiting] = useState(false)
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
  const displayDiagnosticLogs =
    state.diagnosticLogs.length > 0
      ? state.diagnosticLogs
      : state.session?.diagnosticLogs ?? []
  const captchaPending = isCaptchaPending(
    displayLogs,
    state.session?.currentStep,
  )

  usePinnedScroll(logViewRef, displayLogs.length)
  usePinnedScroll(diagnosticViewRef, diagnosticExpanded ? displayDiagnosticLogs.length : 0)

  useEffect(() => {
    if (!menuOpen) return
    const onDocClick = (event: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setMenuOpen(false)
      }
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [menuOpen])

  useEffect(() => {
    if (!previewOpen || clickPending) {
      if (!previewOpen) setPreviewWaiting(false)
      return
    }
    let cancelled = false
    let attempts = 0

    const pullFrame = async () => {
      attempts++
      try {
        const frame = await fetchPreviewFrame(config.profileId)
        if (!cancelled && frame) {
          onScreenshotUpdate(frame)
          setPreviewWaiting(false)
        } else if (!cancelled && attempts >= 8) {
          setPreviewWaiting(true)
        }
      } catch {
        if (!cancelled && attempts >= 8) setPreviewWaiting(true)
      }
    }

    void pullFrame()
    const intervalMs = captchaPending ? 1200 : 800
    const id = window.setInterval(() => void pullFrame(), intervalMs)
    return () => {
      cancelled = true
      window.clearInterval(id)
    }
  }, [previewOpen, clickPending, captchaPending, config.profileId, onScreenshotUpdate])

  useEffect(() => {
    if (!previewFullscreen) return
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = prev
    }
  }, [previewFullscreen])

  useEffect(() => {
    if (!previewFullscreen) return
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setPreviewFullscreen(false)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [previewFullscreen])

  useEffect(() => {
    if (!isOccupied && previewOpen) {
      setPreviewOpen(false)
      setPreviewFullscreen(false)
      onPreviewClose()
    }
  }, [isOccupied, previewOpen, onPreviewClose])

  const closePreview = () => {
    setPreviewOpen(false)
    setPreviewFullscreen(false)
    onPreviewClose()
  }

  const openPreview = async () => {
    setPreviewOpen(true)
    setPreviewLoading(true)
    try {
      await setPreviewByProfile(config.profileId, true)
      const frame = await fetchPreviewFrame(config.profileId)
      if (frame) onScreenshotUpdate(frame)
    } catch {
      setPreviewOpen(false)
    } finally {
      setPreviewLoading(false)
    }
  }

  const togglePreview = () => {
    if (previewOpen) {
      closePreview()
      return
    }
    void openPreview()
  }

  const toggleFullscreen = () => {
    setPreviewFullscreen((prev) => !prev)
  }

  useEffect(() => {
    if (!captchaPending || previewOpen || !isOccupied) return
    void openPreview()
  }, [captchaPending, previewOpen, isOccupied, config.profileId, onScreenshotUpdate])

  const handleScreenshotClick = async (event: React.MouseEvent<HTMLImageElement>) => {
    const img = screenshotRef.current
    if (!img || clickPending) return

    const ratios = getImageClickRatios(img, event.clientX, event.clientY)
    if (!ratios) return

    setClickPending(true)
    setClickError(null)
    try {
      await previewClickByProfile(config.profileId, ratios.xRatio, ratios.yRatio)
      try {
        const frame = await fetchPreviewFrame(config.profileId)
        if (frame) onScreenshotUpdate(frame)
      } catch {
        /* ignore frame refresh errors */
      }
    } catch (e) {
      setClickError(e instanceof Error ? e.message : 'Не удалось отправить клик')
      window.setTimeout(() => setClickError(null), 4000)
    } finally {
      window.setTimeout(() => setClickPending(false), 400)
    }
  }

  const refreshPreviewFrame = async () => {
    try {
      const frame = await fetchPreviewFrame(config.profileId)
      if (frame) onScreenshotUpdate(frame)
    } catch {
      /* ignore frame refresh errors */
    }
  }

  const runPreviewAction = async (action: 'reload' | 'closeCaptcha') => {
    if (!isOccupied || previewActionPending) return

    setMenuOpen(false)
    setPreviewActionPending(true)
    setPreviewActionError(null)
    try {
      if (action === 'reload') {
        await previewReloadByProfile(config.profileId)
      } else {
        await previewCloseCaptchaTabByProfile(config.profileId)
      }
      await refreshPreviewFrame()
    } catch (e) {
      setPreviewActionError(
        e instanceof Error ? e.message : 'Не удалось выполнить действие в браузере',
      )
      window.setTimeout(() => setPreviewActionError(null), 5000)
    } finally {
      setPreviewActionPending(false)
    }
  }

  const loadBrowserTabs = async () => {
    setTabsLoading(true)
    setTabsError(null)
    try {
      const tabs = await fetchBrowserTabsByProfile(config.profileId)
      setBrowserTabs(tabs)
    } catch (e) {
      setTabsError(e instanceof Error ? e.message : 'Не удалось загрузить вкладки')
      setBrowserTabs([])
    } finally {
      setTabsLoading(false)
    }
  }

  const openTabsPopup = () => {
    setMenuOpen(false)
    setTabsOpen(true)
    void loadBrowserTabs()
  }

  const closeTabsPopup = () => {
    setTabsOpen(false)
    setTabsError(null)
    setBrowserTabs([])
    setTabClosePending(null)
  }

  const handleCloseTab = async (index: number) => {
    if (tabClosePending !== null) return

    setTabClosePending(index)
    setTabsError(null)
    try {
      await closeBrowserTabByProfile(config.profileId, index)
      await loadBrowserTabs()
      await refreshPreviewFrame()
    } catch (e) {
      setTabsError(e instanceof Error ? e.message : 'Не удалось закрыть вкладку')
    } finally {
      setTabClosePending(null)
    }
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

  const copyDiagnosticLogs = async () => {
    if (displayDiagnosticLogs.length === 0) return
    const text = displayDiagnosticLogs.join('\n\n')
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
      setDiagnosticCopied(true)
      window.setTimeout(() => setDiagnosticCopied(false), 2000)
    } catch {
      /* ignore */
    }
  }

  return (
    <section
      ref={panelRef}
      className={`session-panel${highlightError ? ' session-panel-error-highlight' : ''}`}
    >
      <div className="session-panel-header">
        <div>
          <h2>{config.label}</h2>
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

      <div className="schedule-block">
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
        </div>
        <div className="schedule-row schedule-row-stop">
          <label className="schedule-label schedule-label-inline">Остановить (МСК)</label>
          <div className="schedule-time-wrap">
            <input
              type="time"
              className="schedule-time"
              value={config.stopAtMsk ?? ''}
              onChange={(e) => onSlotChange({ stopAtMsk: e.target.value || null })}
            />
            {config.stopAtMsk ? (
              <button
                type="button"
                className="schedule-time-clear"
                onClick={() => onSlotChange({ stopAtMsk: null })}
                title="Сбросить время остановки"
                aria-label="Сбросить время остановки"
              >
                ×
              </button>
            ) : null}
          </div>
        </div>
      </div>

      <div className="schedule-row">
        <label className="schedule-label proxy-select-label">
          Устройство
          <select
            className="proxy-select"
            value={config.devicePlatform ?? 'Random'}
            onChange={(e) =>
              onSlotChange({ devicePlatform: e.target.value as SessionDevicePlatform })
            }
          >
            {DEVICE_PLATFORM_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
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
          <div className="schedule-label proxy-select-label">
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
          </div>
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
        ) : null}
        <button className="btn btn-danger btn-sm" onClick={onStop} disabled={state.loading || !isOccupied}>
          ■ Стоп
        </button>
        <div className="session-menu" ref={menuRef}>
          <button
            type="button"
            className="btn btn-secondary btn-sm session-menu-trigger"
            onClick={() => setMenuOpen((open) => !open)}
            aria-label="Дополнительные действия"
            aria-expanded={menuOpen}
          >
            ⋯
          </button>
          {menuOpen ? (
            <div className="session-menu-dropdown" role="menu">
              {!isPaused ? (
                <button
                  type="button"
                  className="session-menu-item"
                  role="menuitem"
                  disabled={state.loading || !isRunning}
                  onClick={() => {
                    setMenuOpen(false)
                    onPause()
                  }}
                >
                  ⏸ Пауза
                </button>
              ) : null}
              <button
                type="button"
                className="session-menu-item"
                role="menuitem"
                disabled={!isOccupied}
                onClick={() => {
                  setMenuOpen(false)
                  void togglePreview()
                }}
              >
                Просмотр
              </button>
              <button
                type="button"
                className="session-menu-item"
                role="menuitem"
                disabled={!isOccupied || previewActionPending}
                onClick={() => void runPreviewAction('reload')}
              >
                ↻ Обновить страницу
              </button>
              <button
                type="button"
                className="session-menu-item"
                role="menuitem"
                disabled={!isOccupied}
                onClick={openTabsPopup}
              >
                Вкладки браузера
              </button>
              <button
                type="button"
                className="session-menu-item"
                role="menuitem"
                disabled={!isOccupied || !captchaPending || previewActionPending}
                onClick={() => void runPreviewAction('closeCaptcha')}
              >
                ✕ Закрыть вкладку капчи
              </button>
              <button
                type="button"
                className="session-menu-item session-menu-item-danger"
                role="menuitem"
                disabled={!canDelete || state.loading}
                onClick={() => {
                  setMenuOpen(false)
                  onDelete()
                }}
              >
                Удалить
              </button>
            </div>
          ) : null}
        </div>
        <span className="step-hint">
          {state.session?.currentStep
            ? `Шаг ${state.session.currentStep}/${state.session.totalSteps}`
            : '—'}
        </span>
      </div>

      <div className="progress-bar progress-bar-sm">
        <div className="progress-fill" style={{ width: `${progress}%` }} />
      </div>

      {previewActionError && isOccupied ? (
        <div className="preview-click-error">{previewActionError}</div>
      ) : null}

      {captchaPending && !previewOpen && isOccupied && (
        <div className="captcha-banner">
          Обнаружена Captcha Verification — все действия остановлены. Откройте «Просмотр», «На весь экран» и пройдите проверку вручную
        </div>
      )}

      {previewOpen &&
        (() => {
          const previewPanel = (
            <div
              className={`session-preview${previewFullscreen ? ' session-preview-fullscreen' : ''}`}
            >
              <div className="session-preview-header">
                <div className="session-preview-title">
                  <span>
                    Экран браузера
                    {captchaPending ? ' — кликните по капче' : ''}
                  </span>
                  <button
                    type="button"
                    className="preview-help-btn"
                    title={
                      captchaPending
                        ? 'Кликните по галочке «Я не робот». Клики по изображению передаются в браузер. «Закрыть просмотр» — выключить трансляцию. Esc — выйти из полного экрана.'
                        : 'Клики по изображению передаются в браузер. «Закрыть просмотр» — выключить трансляцию'
                    }
                    aria-label="Подсказка по просмотру"
                  >
                    ?
                  </button>
                </div>
                <div className="session-preview-actions">
                  <button
                    type="button"
                    className="btn-icon"
                    onClick={toggleFullscreen}
                    title={previewFullscreen ? 'Выйти из полного экрана (Esc)' : 'Развернуть на весь экран'}
                    aria-label={previewFullscreen ? 'Выйти из полного экрана' : 'На весь экран'}
                  >
                    {previewFullscreen ? (
                      <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                        <path
                          fill="currentColor"
                          d="M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z"
                        />
                      </svg>
                    ) : (
                      <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                        <path
                          fill="currentColor"
                          d="M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z"
                        />
                      </svg>
                    )}
                  </button>
                  <button
                    type="button"
                    className="btn-icon"
                    onClick={closePreview}
                    title="Закрыть просмотр"
                    aria-label="Закрыть просмотр"
                  >
                    <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                      <path
                        fill="currentColor"
                        d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"
                      />
                    </svg>
                  </button>
                </div>
              </div>
              {clickError && <div className="preview-click-error">{clickError}</div>}
              <div className={`browser-viewport${clickPending ? ' browser-viewport-clicking' : ''}`}>
                {previewLoading ? (
                  <span className="screenshot-placeholder">Подключение просмотра…</span>
                ) : state.screenshotBase64 ? (
                  <img
                    ref={screenshotRef}
                    className="screenshot screenshot-interactive"
                    src={`data:image/jpeg;base64,${state.screenshotBase64}`}
                    alt="Экран браузера — кликните для взаимодействия"
                    title="Клик отправляется в браузер сессии"
                    onClick={(e) => void handleScreenshotClick(e)}
                  />
                ) : previewWaiting ? (
                  <span className="screenshot-placeholder">
                    Кадр не получен — подождите или перезапустите «Просмотр». Если сессия на капче, пройдите её здесь.
                  </span>
                ) : (
                  <span className="screenshot-placeholder">Ожидание кадра… (обновление каждые 0,8 сек)</span>
                )}
              </div>
            </div>
          )

          return previewFullscreen
            ? createPortal(previewPanel, document.body)
            : previewPanel
        })()}

      <div className="log-panel-header">
        <span className="log-panel-title">Лог</span>
        <div className="log-panel-actions">
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
          <button
            type="button"
            className="btn-icon"
            onClick={onClearLogs}
            disabled={displayLogs.length === 0 && displayDiagnosticLogs.length === 0}
            title="Очистить лог и диагностику"
            aria-label="Очистить лог и диагностику"
          >
            <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
              <path
                fill="currentColor"
                d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"
              />
            </svg>
          </button>
        </div>
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

      {displayDiagnosticLogs.length > 0 && (
        <div className={`diagnostic-section${diagnosticExpanded ? '' : ' is-collapsed'}`}>
          <div className="log-panel-header diagnostic-log-header">
            <button
              type="button"
              className="log-panel-toggle"
              onClick={() => setDiagnosticExpanded((v) => !v)}
              aria-expanded={diagnosticExpanded}
            >
              <span className={`log-panel-chevron${diagnosticExpanded ? ' expanded' : ''}`} aria-hidden="true">
                ▸
              </span>
              <span className="log-panel-title">Диагностика</span>
            </button>
            <div className="log-panel-actions">
              <button
                type="button"
                className={`btn-icon${diagnosticCopied ? ' btn-icon-ok' : ''}`}
                onClick={() => void copyDiagnosticLogs()}
                title={diagnosticCopied ? 'Скопировано' : 'Копировать диагностику'}
                aria-label="Копировать диагностику"
              >
                {diagnosticCopied ? (
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
              <button
                type="button"
                className="btn-icon"
                onClick={onClearLogs}
                title="Очистить диагностику"
                aria-label="Очистить диагностику"
              >
                <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                  <path
                    fill="currentColor"
                    d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"
                  />
                </svg>
              </button>
            </div>
          </div>
          {diagnosticExpanded ? (
            <div ref={diagnosticViewRef} className="log-view session-log diagnostic-log">
              {displayDiagnosticLogs.map((entry, i) => (
                <pre key={i} className="diagnostic-entry">
                  {entry}
                </pre>
              ))}
            </div>
          ) : null}
        </div>
      )}

      {tabsOpen ? (
        <div className="popup-overlay" onClick={closeTabsPopup}>
          <div
            className="popup-dialog popup-dialog-wide"
            role="dialog"
            aria-labelledby={`tabs-title-${config.profileId}`}
            onClick={(e) => e.stopPropagation()}
          >
            <div className="browser-tabs-header">
              <h3 id={`tabs-title-${config.profileId}`}>Вкладки браузера</h3>
              <button
                type="button"
                className="btn-icon browser-tabs-refresh"
                onClick={() => void loadBrowserTabs()}
                disabled={tabsLoading}
                title="Обновить список"
                aria-label="Обновить список вкладок"
              >
                ↻
              </button>
            </div>
            {tabsError ? <p className="browser-tabs-error">{tabsError}</p> : null}
            {tabsLoading && browserTabs.length === 0 ? (
              <p className="browser-tabs-empty">Загрузка…</p>
            ) : browserTabs.length === 0 ? (
              <p className="browser-tabs-empty">Нет открытых вкладок</p>
            ) : (
              <ul className="browser-tabs-list">
                {browserTabs.map((tab) => (
                  <li
                    key={`${tab.index}-${tab.url}`}
                    className={`browser-tabs-item${tab.isActive ? ' browser-tabs-item-active' : ''}`}
                  >
                    <div className="browser-tabs-item-main">
                      <div className="browser-tabs-item-title">
                        {tab.title || 'Без названия'}
                        {tab.isCaptcha ? <span className="browser-tabs-badge">капча</span> : null}
                        {tab.isActive ? <span className="browser-tabs-badge">активна</span> : null}
                      </div>
                      <div className="browser-tabs-item-url" title={tab.url}>
                        {tab.url || 'about:blank'}
                      </div>
                    </div>
                    <button
                      type="button"
                      className="browser-tabs-close"
                      onClick={() => void handleCloseTab(tab.index)}
                      disabled={tabClosePending !== null}
                      title="Закрыть вкладку"
                      aria-label="Закрыть вкладку"
                    >
                      {tabClosePending === tab.index ? '…' : '✕'}
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <div className="popup-actions">
              <button type="button" className="btn btn-secondary btn-sm" onClick={closeTabsPopup}>
                Закрыть
              </button>
            </div>
          </div>
        </div>
      ) : null}
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

function getImageClickRatios(
  img: HTMLImageElement,
  clientX: number,
  clientY: number,
): { xRatio: number; yRatio: number } | null {
  const rect = img.getBoundingClientRect()
  const naturalW = img.naturalWidth
  const naturalH = img.naturalHeight
  if (!naturalW || !naturalH || rect.width <= 0 || rect.height <= 0) return null

  const scale = Math.min(rect.width / naturalW, rect.height / naturalH)
  const renderedW = naturalW * scale
  const renderedH = naturalH * scale
  const offsetX = (rect.width - renderedW) / 2
  const offsetY = (rect.height - renderedH) / 2

  const localX = clientX - rect.left - offsetX
  const localY = clientY - rect.top - offsetY
  if (localX < 0 || localY < 0 || localX > renderedW || localY > renderedH) return null

  return {
    xRatio: Math.min(1, Math.max(0, localX / renderedW)),
    yRatio: Math.min(1, Math.max(0, localY / renderedH)),
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
