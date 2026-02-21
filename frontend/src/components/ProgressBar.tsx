interface ProgressBarProps {
  progress: number
  label?: string
  status?: 'uploading' | 'hashing' | 'completed' | 'error'
}

const STATUS_COLORS: Record<
  NonNullable<ProgressBarProps['status']>,
  string
> = {
  uploading: '#2563eb',
  hashing: '#d97706',
  completed: '#059669',
  error: '#dc2626',
}

export function ProgressBar({
  progress,
  label,
  status = 'uploading',
}: ProgressBarProps) {
  const normalizedProgress = Math.min(Math.max(progress, 0), 100)

  return (
    <div className="progress-wrapper">
      {label ? <p className="progress-label">{label}</p> : null}
      <div aria-label="progress" className="progress" role="progressbar">
        <div
          className="progress-fill"
          style={{
            backgroundColor: STATUS_COLORS[status],
            width: `${normalizedProgress}%`,
          }}
        />
      </div>
      <span className="progress-value">{normalizedProgress.toFixed(1)}%</span>
    </div>
  )
}
