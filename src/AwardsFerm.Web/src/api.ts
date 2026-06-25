import type {
  AdAccount,
  BrowserTabInfo,
  CreateAdAccountRequest,
  CreateProxyRequest,
  ProxyConfig,
  SessionDevicePlatform,
  SessionEvent,
  SessionInfo,
  SessionSlotConfig,
  UpdateAdAccountRequest,
  UserProfitSummary,
} from './types'
import { getToken, clearAuth } from './auth'
import { normalizeEventType, normalizeSession, normalizeStatus } from './utils/session'

const apiBase = (import.meta.env.VITE_API_URL ?? '').replace(/\/$/, '')

function apiPath(path: string): string {
  const normalized = path.startsWith('/') ? path : `/${path}`
  return apiBase ? `${apiBase}${normalized}` : normalized
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers)
  const token = getToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  if (init.body && !headers.has('Content-Type'))
    headers.set('Content-Type', 'application/json')

  const res = await fetch(apiPath(path), { ...init, headers })

  if (res.status === 401) {
    clearAuth()
    throw new ApiError('Требуется авторизация', 401)
  }

  return res
}

export async function checkApiHealth(): Promise<boolean> {
  try {
    const res = await fetch(apiPath('/api/health'), { signal: AbortSignal.timeout(3000) })
    return res.ok
  } catch {
    return false
  }
}

export async function login(loginName: string, password: string): Promise<{ token: string; login: string }> {
  const res = await fetch(apiPath('/api/auth/login'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ login: loginName, password }),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Неверный логин или пароль')
  }
  return (await res.json()) as { token: string; login: string }
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  const res = await apiFetch('/api/auth/change-password', {
    method: 'POST',
    body: JSON.stringify({ currentPassword, newPassword }),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось сменить пароль')
  }
}

export async function fetchAdAccounts(): Promise<AdAccount[]> {
  const res = await apiFetch('/api/adaccounts')
  if (!res.ok) throw new Error('Не удалось загрузить рекламные аккаунты')
  return (await res.json()) as AdAccount[]
}

export async function createAdAccount(body: CreateAdAccountRequest): Promise<AdAccount> {
  const res = await apiFetch('/api/adaccounts', { method: 'POST', body: JSON.stringify(body) })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось создать аккаунт')
  }
  return (await res.json()) as AdAccount
}

export async function updateAdAccount(id: number, body: UpdateAdAccountRequest): Promise<AdAccount> {
  const res = await apiFetch(`/api/adaccounts/${id}`, { method: 'PATCH', body: JSON.stringify(body) })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось обновить аккаунт')
  }
  return (await res.json()) as AdAccount
}

export async function deleteAdAccount(id: number): Promise<void> {
  const res = await apiFetch(`/api/adaccounts/${id}`, { method: 'DELETE' })
  if (!res.ok) throw new Error('Не удалось удалить аккаунт')
}

export async function fetchUserProfit(): Promise<UserProfitSummary> {
  const res = await apiFetch('/api/userprofit')
  if (!res.ok) throw new Error('Не удалось загрузить прибыль')
  return (await res.json()) as UserProfitSummary
}

export async function fetchSessions(): Promise<SessionInfo[]> {
  const res = await apiFetch('/api/sessions')
  if (!res.ok) throw new Error('Не удалось получить список сессий')
  const data = (await res.json()) as SessionInfo[]
  return data.map(normalizeSession)
}

export async function downloadSessionLog(sessionId: string): Promise<Blob> {
  const res = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/log`)
  if (!res.ok) {
    const text = await res.text().catch(() => '')
    throw new ApiError(text || 'Не удалось скачать лог', res.status)
  }
  return res.blob()
}

export async function startSession(
  adAccountId: number,
  profileId: string,
  options: {
    gameTitle: string
    gameUrl: string
    stopAtMsk?: string | null
    autoRestart?: boolean
    proxyEnabled?: boolean
    devicePlatform?: SessionDevicePlatform
  },
): Promise<SessionInfo> {
  const gameUrlPart = extractGameUrlPart(options.gameUrl)
  const searchQuery = options.gameTitle.split(/\s+/)[0]?.toLowerCase() || 'игра'

  const res = await apiFetch('/api/sessions/start', {
    method: 'POST',
    body: JSON.stringify({
      adAccountId,
      profileId,
      stopAtMsk: options.stopAtMsk ?? null,
      autoRestart: options.autoRestart ?? true,
      options: {
        searchQuery,
        targetGameTitle: options.gameTitle,
        targetGameUrlPart: gameUrlPart,
        playDurationMinSeconds: 120,
        playDurationMaxSeconds: 180,
        headless: false,
        useProxy: options.proxyEnabled ?? true,
        devicePlatform: options.devicePlatform ?? 'Random',
      },
    }),
  })

  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось запустить сессию')
  }
  return normalizeSession((await res.json()) as SessionInfo)
}

export async function stopSessionByProfile(profileId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/profile/${encodeURIComponent(profileId)}/stop`, { method: 'POST' })
  if (!res.ok) throw new Error('Не удалось остановить сессию')
}

/** @deprecated используйте stopSessionByProfile */
export async function stopSession(sessionId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/${sessionId}/stop`, { method: 'POST' })
  if (!res.ok) throw new Error('Не удалось остановить сессию')
}

export async function pauseSessionByProfile(profileId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/profile/${encodeURIComponent(profileId)}/pause`, { method: 'POST' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось поставить сессию на паузу')
  }
}

/** @deprecated используйте pauseSessionByProfile */
export async function pauseSession(sessionId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/${sessionId}/pause`, { method: 'POST' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось поставить сессию на паузу')
  }
}

export async function resumeSessionByProfile(profileId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/profile/${encodeURIComponent(profileId)}/resume`, { method: 'POST' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось продолжить сессию')
  }
}

export async function setPreviewByProfile(profileId: string, enabled: boolean): Promise<void> {
  const res = await apiFetch(`/api/sessions/profile/${encodeURIComponent(profileId)}/preview`, {
    method: 'POST',
    body: JSON.stringify({ enabled }),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось переключить просмотр')
  }
}

export async function fetchPreviewFrame(profileId: string): Promise<string | null> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/frame`,
    { signal: AbortSignal.timeout(10_000) },
  )
  if (res.status === 204) return null
  if (!res.ok) return null
  const data = (await res.json()) as { imageBase64?: string }
  return data.imageBase64 ?? null
}

export async function previewClickByProfile(
  profileId: string,
  xRatio: number,
  yRatio: number,
): Promise<void> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/click`,
    {
      method: 'POST',
      body: JSON.stringify({ xRatio, yRatio }),
    },
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось отправить клик')
  }
}

export async function previewReloadByProfile(profileId: string): Promise<void> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/reload`,
    { method: 'POST' },
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось обновить страницу')
  }
}

export async function previewReloadTabByProfile(profileId: string, index: number): Promise<void> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/tabs/${index}/reload`,
    { method: 'POST' },
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось обновить вкладку')
  }
}

export async function previewCloseCaptchaTabByProfile(profileId: string): Promise<void> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/close-captcha-tab`,
    { method: 'POST' },
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось закрыть вкладку капчи')
  }
}

export async function fetchBrowserTabsByProfile(profileId: string): Promise<BrowserTabInfo[]> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/tabs`,
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось получить список вкладок')
  }
  return (await res.json()) as BrowserTabInfo[]
}

export async function closeBrowserTabByProfile(profileId: string, index: number): Promise<void> {
  const res = await apiFetch(
    `/api/sessions/profile/${encodeURIComponent(profileId)}/preview/tabs/${index}`,
    { method: 'DELETE' },
  )
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось закрыть вкладку')
  }
}

/** @deprecated используйте resumeSessionByProfile */
export async function resumeSession(sessionId: string): Promise<void> {
  const res = await apiFetch(`/api/sessions/${sessionId}/resume`, { method: 'POST' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось продолжить сессию')
  }
}

export async function fetchSlots(adAccountId: number): Promise<SessionSlotConfig[]> {
  const res = await apiFetch(`/api/slots?adAccountId=${adAccountId}`)
  if (!res.ok) throw new Error('Не удалось загрузить слоты сессий')
  return (await res.json()) as SessionSlotConfig[]
}

export async function addSlot(adAccountId: number, label?: string): Promise<SessionSlotConfig> {
  const res = await apiFetch('/api/slots', {
    method: 'POST',
    body: JSON.stringify({ adAccountId, label }),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось добавить сессию')
  }
  return (await res.json()) as SessionSlotConfig
}

export async function updateSlot(
  adAccountId: number,
  profileId: string,
  patch: Partial<
    Pick<SessionSlotConfig, 'label' | 'scheduleEnabled' | 'scheduledStartMsk' | 'stopAtMsk' | 'autoRestart' | 'proxyEnabled' | 'proxyId' | 'devicePlatform'>
  >,
): Promise<SessionSlotConfig> {
  const res = await apiFetch(`/api/slots/${profileId}?adAccountId=${adAccountId}`, {
    method: 'PATCH',
    body: JSON.stringify(patch),
  })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось обновить сессию')
  }
  return (await res.json()) as SessionSlotConfig
}

export async function deleteSlot(adAccountId: number, profileId: string): Promise<void> {
  const res = await apiFetch(`/api/slots/${profileId}?adAccountId=${adAccountId}`, { method: 'DELETE' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось удалить сессию')
  }
}

export async function fetchProxies(): Promise<ProxyConfig[]> {
  const res = await apiFetch('/api/proxies')
  if (!res.ok) throw new Error('Не удалось загрузить прокси')
  return (await res.json()) as ProxyConfig[]
}

export async function createProxy(body: CreateProxyRequest): Promise<ProxyConfig> {
  const res = await apiFetch('/api/proxies', { method: 'POST', body: JSON.stringify(body) })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось добавить прокси')
  }
  return (await res.json()) as ProxyConfig
}

export async function deleteProxy(proxyId: number): Promise<void> {
  const res = await apiFetch(`/api/proxies/${proxyId}`, { method: 'DELETE' })
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || 'Не удалось удалить прокси')
  }
}

function extractGameUrlPart(gameUrl: string): string {
  try {
    const url = new URL(gameUrl)
    const parts = url.pathname.split('/').filter(Boolean)
    return parts[parts.length - 1] ?? gameUrl
  } catch {
    const parts = gameUrl.split('/').filter(Boolean)
    return parts[parts.length - 1] ?? gameUrl
  }
}

// SignalR
import * as signalR from '@microsoft/signalr'

let hubConnection: signalR.HubConnection | null = null
let hubHandler: ((event: SessionEvent) => void) | null = null

function getHubConnection(): signalR.HubConnection {
  if (!hubConnection) {
    const hubUrl = apiPath('/hubs/session')
    const token = getToken()
    hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, token ? { accessTokenFactory: () => token } : undefined)
      .withAutomaticReconnect()
      .build()
  }
  return hubConnection
}

export function createSessionHub(onEvent: (event: SessionEvent) => void): signalR.HubConnection {
  const hub = getHubConnection()
  hubHandler = onEvent

  hub.off('SessionEvent')
  hub.on('SessionEvent', (raw: SessionEvent) => {
    onEvent({
      ...raw,
      type: normalizeEventType(raw.type),
      status: raw.status !== undefined ? normalizeStatus(raw.status) : undefined,
    })
  })

  return hub
}

export function isHubConnected(): boolean {
  return hubConnection?.state === signalR.HubConnectionState.Connected
}

export async function ensureHubConnected(): Promise<boolean> {
  const apiUp = await checkApiHealth()
  if (!apiUp) return false

  const hub = getHubConnection()
  if (hubHandler) {
    hub.off('SessionEvent')
    hub.on('SessionEvent', (raw: SessionEvent) => {
      hubHandler?.({
        ...raw,
        type: normalizeEventType(raw.type),
        status: raw.status !== undefined ? normalizeStatus(raw.status) : undefined,
      })
    })
  }

  if (hub.state === signalR.HubConnectionState.Disconnected) {
    try {
      await hub.start()
    } catch {
      return false
    }
  }

  return hub.state === signalR.HubConnectionState.Connected
}

export function resetHubConnection(): void {
  if (hubConnection) {
    void hubConnection.stop()
    hubConnection = null
  }
  hubHandler = null
}
