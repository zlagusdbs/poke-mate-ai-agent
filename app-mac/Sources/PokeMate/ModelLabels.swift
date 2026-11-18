import PokeMateCore
import SwiftUI

enum ModelLabels {
    static let models = ["": "기본 (계정 기본)", "opus": "Opus", "sonnet": "Sonnet", "fable": "Fable"]
    static let efforts = ["": "기본", "low": "낮음", "medium": "보통", "high": "높음", "xhigh": "아주 높음", "max": "최대"]

    static func shortModel(_ key: String) -> String { key.isEmpty ? "기본 모델" : models[key] ?? key }
    static func shortEffort(_ key: String) -> String { key.isEmpty ? "기본 추론" : efforts[key] ?? key }
}
