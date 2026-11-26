use airplay_core::{AirPlayError, Result, SampleRate};
use rubato::{FftFixedIn, Resampler};

/// High-quality stereo audio resampler supporting both 44.1 kHz and 48 kHz output targets,
/// with zero-overhead passthrough when input sample rate already matches the target rate.
pub struct StereoResampler {
    input_rate: u32,
    output_rate: SampleRate,
    chunk_frames: usize,
    inner: Option<FftFixedIn<f32>>,
    in_left: Vec<f32>,
    in_right: Vec<f32>,
}

impl StereoResampler {
    pub fn new(input_rate: u32, output_rate: SampleRate) -> Result<Self> {
        if input_rate == 0 {
            return Err(AirPlayError::InvalidInput("input_rate must be > 0".into()));
        }
        // Keep chunk size small (256 frames = ~5.3 ms @ 48 kHz) to minimize end-to-end latency.
        let chunk_frames = 256;
        let inner = if input_rate == output_rate.hz() {
            None
        } else {
            let r = FftFixedIn::<f32>::new(
                input_rate as usize,
                output_rate.hz() as usize,
                chunk_frames,
                1,
                2,
            )
            .map_err(|e| AirPlayError::Audio(format!("Resampler init failed: {e}")))?;
            Some(r)
        };
        Ok(Self {
            input_rate,
            output_rate,
            chunk_frames,
            inner,
            in_left: Vec::with_capacity(chunk_frames * 2),
            in_right: Vec::with_capacity(chunk_frames * 2),
        })
    }

    pub fn input_rate(&self) -> u32 {
        self.input_rate
    }

    pub fn output_rate(&self) -> SampleRate {
        self.output_rate
    }

    pub fn is_passthrough(&self) -> bool {
        self.inner.is_none()
    }

    /// Process interleaved stereo f32 frames (`[L0, R0, L1, R1, ...]`) and return resampled interleaved stereo f32 frames.
    pub fn process_interleaved(&mut self, input: &[f32]) -> Result<Vec<f32>> {
        if self.inner.is_none() {
            return Ok(input.to_vec());
        }
        for pair in input.chunks_exact(2) {
            self.in_left.push(pair[0]);
            self.in_right.push(pair[1]);
        }
        let resampler = self.inner.as_mut().unwrap();
        let mut out = Vec::new();
        while self.in_left.len() >= self.chunk_frames {
            let l_chunk: Vec<f32> = self.in_left.drain(..self.chunk_frames).collect();
            let r_chunk: Vec<f32> = self.in_right.drain(..self.chunk_frames).collect();
            let waves = vec![l_chunk, r_chunk];
            let res = resampler
                .process(&waves, None)
                .map_err(|e| AirPlayError::Audio(format!("Resampler process failed: {e}")))?;
            let n = res[0].len().min(res[1].len());
            out.reserve(n * 2);
            for i in 0..n {
                out.push(res[0][i]);
                out.push(res[1][i]);
            }
        }
        Ok(out)
    }

    pub fn reset(&mut self) {
        self.in_left.clear();
        self.in_right.clear();
        if let Some(r) = &mut self.inner {
            r.reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn passthrough_48khz_is_bit_exact() {
        let mut r = StereoResampler::new(48_000, SampleRate::Hz48000).unwrap();
        assert!(r.is_passthrough());
        let input = vec![0.25f32, -0.5, 0.75, -0.125];
        let out = r.process_interleaved(&input).unwrap();
        assert_eq!(out, input);
    }

    #[test]
    fn resamples_48khz_to_44100hz() {
        let mut r = StereoResampler::new(48_000, SampleRate::Hz44100).unwrap();
        assert!(!r.is_passthrough());
        let mut input = Vec::with_capacity(4800 * 2);
        for i in 0..4800 {
            let s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / 48_000.0).sin() * 0.5;
            input.push(s);
            input.push(s);
        }
        let out = r.process_interleaved(&input).unwrap();
        assert!(out.len() > 3000 * 2);
    }
}
