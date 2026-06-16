export type SessionStatus =
  | 'Idle'
  | 'Starting'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'Stopped'
  | 'Paused'

export interface SessionInfo {
  id: string
  adAccountId?: number
  profileId: string
  stopAtMsk?: string | null
  autoRestart: boolean
  status: SessionStatus
  currentStep: number
  totalSteps: number
  currentStepName: string
  errorMessage?: string
  publicIp?: string
  trafficBytes?: number
  startedAt?: string
  finishedAt?: string
  logs: string[]
}

export type SessionEventType =
  | 'Log'
  | 'StepChanged'
  | 'Screenshot'
  | 'StatusChanged'
  | 'IpDetected'
  | 'TrafficUpdated'
  | 'Completed'
  | 'Failed'

export interface SessionEvent {
  sessionId: string
  type: SessionEventType
  message?: string
  currentStep?: number
  totalSteps?: number
  stepName?: string
  status?: SessionStatus
  publicIp?: string
  trafficBytes?: number
  screenshotBase64?: string
  timestamp: string
}

export interface SessionSlotConfig {
  id?: number
  adAccountId?: number
  profileId: string
  label: string
  scheduleEnabled: boolean
  scheduledStartMsk?: string | null
  stopAtMsk?: string | null
  autoRestart?: boolean
  proxyEnabled?: boolean
}

export interface SlotState {
  session: SessionInfo | null
  logs: string[]
  loading: boolean
}

export interface AdAccount {
  id: number
  name: string
  gameTitle: string
  gameUrl: string
  todayReward?: number
  monthReward?: number
  createdAt: string
}

export interface CreateAdAccountRequest {
  name: string
  gameTitle: string
  gameUrl: string
  token: string
}

export interface UpdateAdAccountRequest {
  name?: string
  gameTitle?: string
  gameUrl?: string
  token?: string
}

export interface UserProfitSummary {
  totalTodayReward: number
  totalMonthReward: number
  accounts: AdAccount[]
}

export function createEmptySlotState(): SlotState {
  return { session: null, logs: [], loading: false }
}
