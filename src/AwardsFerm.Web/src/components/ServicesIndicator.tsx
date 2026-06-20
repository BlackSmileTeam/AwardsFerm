import { useEffect, useRef, useState } from 'react'

type Props = {
  apiUp: boolean
  signalRUp: boolean
}

export function ServicesIndicator({ apiUp, signalRUp }: Props) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const onPointerDown = (event: MouseEvent | TouchEvent) => {
      if (!rootRef.current?.contains(event.target as Node))
        setOpen(false)
    }

    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('touchstart', onPointerDown)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('touchstart', onPointerDown)
    }
  }, [open])

  return (
    <div
      ref={rootRef}
      className={`services-indicator${open ? ' services-indicator-open' : ''}`}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
    >
      <button
        type="button"
        className="services-indicator-btn"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        aria-haspopup="true"
      >
        Сервисы
      </button>
      <div className="services-tooltip" role="tooltip">
        <div className="services-tooltip-row">
          <span>API</span>
          <span className={apiUp ? 'services-ok' : 'services-bad'}>
            {apiUp ? 'доступен' : 'недоступен'}
          </span>
        </div>
        <div className="services-tooltip-row">
          <span>SignalR</span>
          <span className={signalRUp ? 'services-ok' : 'services-bad'}>
            {signalRUp ? 'доступен' : 'недоступен'}
          </span>
        </div>
      </div>
    </div>
  )
}
