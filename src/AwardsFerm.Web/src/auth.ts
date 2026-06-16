const TOKEN_KEY = 'awardsferm_token'
const LOGIN_KEY = 'awardsferm_login'

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function getLogin(): string | null {
  return localStorage.getItem(LOGIN_KEY)
}

export function setAuth(token: string, login: string): void {
  localStorage.setItem(TOKEN_KEY, token)
  localStorage.setItem(LOGIN_KEY, login)
}

export function clearAuth(): void {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(LOGIN_KEY)
}

export function isAuthenticated(): boolean {
  return !!getToken()
}
