use airplay_core::SampleRate;
use biquad::{Biquad, Coefficients, DirectForm2Transposed, ToHertz, Type};

pub const EQ_FREQUENCIES_HZ: [f32; 10] = [
    31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0,
];

/// 10-band stereo peaking biquad equalizer with preamp gain and dynamic sample-rate recalculation
/// (supports both 44.1 kHz and 48 kHz cleanly).
pub struct Equalizer10Band {
    enabled: bool,
    sample_rate: SampleRate,
    preamp_db: f32,
    gains_db: [f32; 10],
    left: [DirectForm2Transposed<f32>; 10],
    right: [DirectForm2Transposed<f32>; 10],
}

impl Equalizer10Band {
    pub fn new(sample_rate: SampleRate) -> Self {
        let gains_db = [0.0f32; 10];
        let left = build_bank(sample_rate, &gains_db);
        let right = build_bank(sample_rate, &gains_db);
        Self {
            enabled: false,
            sample_rate,
            preamp_db: 0.0,
            gains_db,
            left,
            right,
        }
    }

    pub fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    pub fn set_preamp_db(&mut self, db: f32) {
        self.preamp_db = db.clamp(-24.0, 24.0);
    }

    pub fn set_band_db(&mut self, band: usize, db: f32) {
        if band >= 10 {
            return;
        }
        self.gains_db[band] = db.clamp(-12.0, 12.0);
        let coeffs = band_coeffs(self.sample_rate, band, self.gains_db[band]);
        self.left[band].update_coefficients(coeffs);
        self.right[band].update_coefficients(coeffs);
    }

    pub fn set_sample_rate(&mut self, sample_rate: SampleRate) {
        if self.sample_rate == sample_rate {
            return;
        }
        self.sample_rate = sample_rate;
        self.left = build_bank(sample_rate, &self.gains_db);
        self.right = build_bank(sample_rate, &self.gains_db);
    }

    pub fn reset(&mut self) {
        self.preamp_db = 0.0;
        self.gains_db = [0.0; 10];
        self.left = build_bank(self.sample_rate, &self.gains_db);
        self.right = build_bank(self.sample_rate, &self.gains_db);
    }

    pub fn process_interleaved_in_place(&mut self, stereo: &mut [f32]) {
        if !self.enabled {
            return;
        }
        let all_zero = self.preamp_db.abs() < 0.01 && self.gains_db.iter().all(|g| g.abs() < 0.01);
        if all_zero {
            return;
        }
        let preamp_lin = 10.0f32.powf(self.preamp_db / 20.0);
        for pair in stereo.chunks_exact_mut(2) {
            let mut l = pair[0] * preamp_lin;
            let mut r = pair[1] * preamp_lin;
            for b in 0..10 {
                if self.gains_db[b].abs() >= 0.01 {
                    l = self.left[b].run(l);
                    r = self.right[b].run(r);
                }
            }
            pair[0] = l.clamp(-1.0, 1.0);
            pair[1] = r.clamp(-1.0, 1.0);
        }
    }
}

fn band_coeffs(sr: SampleRate, band: usize, gain_db: f32) -> Coefficients<f32> {
    Coefficients::<f32>::from_params(
        Type::PeakingEQ(gain_db),
        (sr.hz() as f32).hz(),
        EQ_FREQUENCIES_HZ[band].hz(),
        1.41,
    )
    .unwrap()
}

fn build_bank(sr: SampleRate, gains: &[f32; 10]) -> [DirectForm2Transposed<f32>; 10] {
    std::array::from_fn(|i| DirectForm2Transposed::<f32>::new(band_coeffs(sr, i, gains[i])))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn eq_boost_increases_target_band_energy() {
        let sr = SampleRate::Hz48000;
        let mut eq = Equalizer10Band::new(sr);
        eq.set_enabled(true);
        eq.set_band_db(2, 9.0); // 125 Hz +9 dB

        let mut dry = Vec::with_capacity(4800 * 2);
        for i in 0..4800 {
            let s = (2.0 * std::f32::consts::PI * 125.0 * i as f32 / 48_000.0).sin() * 0.2;
            dry.push(s);
            dry.push(s);
        }
        let mut wet = dry.clone();
        eq.process_interleaved_in_place(&mut wet);

        // Compare RMS on the second half (after filter settle)
        let dry_rms: f32 = (dry[4800..].iter().map(|x| x * x).sum::<f32>() / 4800.0).sqrt();
        let wet_rms: f32 = (wet[4800..].iter().map(|x| x * x).sum::<f32>() / 4800.0).sqrt();
        assert!(wet_rms > dry_rms * 2.0, "expected +9 dB boost: dry={dry_rms} wet={wet_rms}");
    }
}
