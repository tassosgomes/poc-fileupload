interface ProgressBarProps {
  progress: number
}

export function ProgressBar({ progress }: ProgressBarProps) {
  return (
    <div aria-label="progress" className="progress">
      <div className="progress-fill" style={{ width: `${progress}%` }} />
    </div>
  )
}
