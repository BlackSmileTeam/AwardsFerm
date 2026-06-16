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
  6: 'Completed',
  7: 'Failed',
}

const EVENT_TYPE_ALIASES: Record<string, SessionEventType> = {
  log: 'Log',
  stepchanged: 'StepChanged',
  screenshot: 'Screenshot',
  statuschanged: 'StatusChanged',
  ipdetected: 'IpDetected',
  trafficupdated: 'TrafficUpdated',
  completed: 'Completed',
  failed: 'Failed',
  Log: 'Log',
  StepChanged: 'StepChanged',
  Screenshot: 'Screenshot',
  StatusChanged: 'StatusChanged',
  IpDetected: 'IpDetected',
  TrafficUpdated: 'TrafficUpdated',
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
