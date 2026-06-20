import { useEffect, useRef, type RefObject } from 'react'
import type { SessionEventType, SessionInfo, SessionStatus } from '../types'

const STATUS_BY_NUMBER: Record<number, SessionStatus> = {
  0: 'Idle',
  1: 'Starting',
  2: 'Running',
  3: 'Completed',
  4: 'Failed',
  5: 'Stopped',
  6: 'Paused',
}

const STATUS_ALIASES: Record<string, SessionStatus> = {
  idle: 'Idle',
  starting: 'Starting',
  running: 'Running',
  completed: 'Completed',
  failed: 'Failed',
  stopped: 'Stopped',
  paused: 'Paused',
  Idle: 'Idle',
  Starting: 'Starting',
  Running: 'Running',
  Completed: 'Completed',
  Failed: 'Failed',
  Stopped: 'Stopped',
  Paused: 'Paused',
}

const EVENT_TYPE_BY_NUMBER: Record<number, SessionEventType> = {
  0: 'Log',
  1: 'StepChanged',
  2: 'Screenshot',
  3: 'StatusChanged',
  4: 'IpDetected',
  5: 'TrafficUpdated',
  6: 'DiagnosticLog',
  7: 'Completed',
  8: 'Failed',
}

const EVENT_TYPE_ALIASES: Record<string, SessionEventType> = {
  log: 'Log',
  stepchanged: 'StepChanged',
  screenshot: 'Screenshot',
  statuschanged: 'StatusChanged',
  ipdetected: 'IpDetected',
  trafficupdated: 'TrafficUpdated',
  diagnosticlog: 'DiagnosticLog',
  completed: 'Completed',
  failed: 'Failed',
  Log: 'Log',
  StepChanged: 'StepChanged',
  Screenshot: 'Screenshot',
  StatusChanged: 'StatusChanged',
  IpDetected: 'IpDetected',
  TrafficUpdated: 'TrafficUpdated',
  DiagnosticLog: 'DiagnosticLog',
  Completed: 'Completed',
  Failed: 'Failed',
}

export function normalizeStatus(status: unknown): SessionStatus {
  if (typeof status === 'number') return STATUS_BY_NUMBER[status] ?? 'Idle'
  if (typeof status === 'string') return STATUS_ALIASES[status] ?? 'Idle'
  return 'Idle'
}

export function normalizeEventType(type: unknown): SessionEventType {
  if (typeof type === 'number') return EVENT_TYPE_BY_NUMBER[type] ?? 'Log'
  if (typeof type === 'string') return EVENT_TYPE_ALIASES[type] ?? 'Log'
  return 'Log'
}

export function normalizeSession(session: SessionInfo): SessionInfo {
  return {
    ...session,
    profileId: session.profileId ?? 'session-001',
    autoRestart: session.autoRestart ?? true,
    trafficBytes: session.trafficBytes ?? 0,
    status: normalizeStatus(session.status),
  }
}

export function statusCssClass(status: unknown): string {
  return normalizeStatus(status).toLowerCase()
}

const CAPTCHA_PENDING_RE =
  /обнаружена капча|автоклик не помог|showcaptcha|капча «я не робот»/i
const CAPTCHA_RESOLVED_RE =
  /капча пройдена|галочка нажата|продолжаем сценарий/i
const PROGRESS_AFTER_CAPTCHA_RE =
  /yandex\.ru открыт|переход на яндекс игры|просмотрено новостей|открыта вкладка игры|шаг [2-9]\/12|шаг 1[0-2]\/12/i

/** Баннер капчи скрывается после прохождения или когда сценарий снова движется вперёд. */
export function isCaptchaPending(logs: string[], currentStep?: number): boolean {
  let pending = false
  let captchaIndex = -1

  for (let i = 0; i < logs.length; i++) {
    const line = logs[i]
    if (CAPTCHA_RESOLVED_RE.test(line)) {
      pending = false
      captchaIndex = -1
      continue
    }
    if (CAPTCHA_PENDING_RE.test(line)) {
      pending = true
      captchaIndex = i
    }
  }

  if (!pending || captchaIndex < 0) return false

  const after = logs.slice(captchaIndex + 1)
  if (after.some((l) => CAPTCHA_RESOLVED_RE.test(l))) return false
  if (after.some((l) => PROGRESS_AFTER_CAPTCHA_RE.test(l))) return false
  if (currentStep != null && currentStep > 1) return false

  return true
}

const SESSION_ERROR_LOG_RE =
  /✗|ошибка:|ERR_|сбой|failed|не удалось/i
const SESSION_ERROR_IGNORE_RE = /будет перезапуск|перезапуск…|перезапущен/i

/** Активная сессия с ошибкой в логе или errorMessage. */
export function hasActiveSessionError(
  status: unknown,
  logs: string[],
  errorMessage?: string | null,
): boolean {
  const normalized = normalizeStatus(status)
  const isActive =
    normalized === 'Starting' || normalized === 'Running' || normalized === 'Paused'
  if (!isActive) return false
  if (errorMessage) return true
  return logs.some(
    (l) =>
      (SESSION_ERROR_LOG_RE.test(l) && !SESSION_ERROR_IGNORE_RE.test(l)) ||
      (/⚠/.test(l) && /диагностик/i.test(l)),
  )
}

/** Автопрокрутка вниз только если пользователь у низа и не выделяет текст. */
export function usePinnedScroll(
  ref: RefObject<HTMLElement | null>,
  contentKey: number,
) {
  const pinnedRef = useRef(true)

  useEffect(() => {
    const el = ref.current
    if (!el) return

    const updatePinned = () => {
      const selection = window.getSelection()
      if (selection && !selection.isCollapsed && el.contains(selection.anchorNode)) {
        pinnedRef.current = false
        return
      }
      const dist = el.scrollHeight - el.scrollTop - el.clientHeight
      pinnedRef.current = dist < 48
    }

    el.addEventListener('scroll', updatePinned, { passive: true })
    document.addEventListener('selectionchange', updatePinned)
    return () => {
      el.removeEventListener('scroll', updatePinned)
      document.removeEventListener('selectionchange', updatePinned)
    }
  }, [ref])

  useEffect(() => {
    if (!pinnedRef.current) return
    const el = ref.current
    if (el) el.scrollTop = el.scrollHeight
  }, [ref, contentKey])
}
